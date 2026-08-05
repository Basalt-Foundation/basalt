using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Basalt.Codec;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Sdk.Wallet.Contracts;

namespace Basalt.Attester;

/// <summary>
/// Talks to a Basalt node: reads whether a label is still reserved, and submits attestations.
///
/// Signing goes through the chain's own <see cref="Transaction.Sign"/> rather than a reimplementation of
/// the payload layout, which is why this tool lives in this repository. A second copy of that layout is
/// a second place for it to drift, and the drift would only show up as transactions nobody accepts.
/// </summary>
public sealed class AttesterChainClient
{
    private const ulong GasLimit = 1_000_000;

    private readonly HttpClient _http;
    private readonly byte[] _signingKey;
    private readonly uint _chainId;
    private readonly Address _sender;

    public AttesterChainClient(HttpClient http, byte[] signingKey, uint chainId)
    {
        _http = http;
        _signingKey = signingKey;
        _chainId = chainId;
        _sender = Ed25519Signer.DeriveAddress(Ed25519Signer.GetPublicKey(signingKey));
    }

    /// <summary>The address this attester signs with, which governance must have authorised.</summary>
    public Address Sender => _sender;

    /// <summary>The name registry, system contract 0x1002.</summary>
    private static Address NameService()
    {
        var bytes = new byte[20];
        bytes[18] = 0x10;
        bytes[19] = 0x02;
        return new Address(bytes);
    }

    private static uint Selector(string method)
    {
        uint hash = 2166136261;
        foreach (char c in method)
        {
            hash ^= (byte)c;
            hash *= 16777619;
        }

        return hash;
    }

    /// <summary>
    /// Whether the name is still unowned, which for a label on the reserved list means it is still
    /// waiting for its claimant.
    ///
    /// This is a filter, not a check. A reservation carries no expiry, so it reads as unregistered here
    /// exactly like a name nobody ever reserved. The contract is what actually refuses to attest on an
    /// unreserved name, and it is the only thing that could enforce that anyway.
    /// </summary>
    public async Task<bool> IsUnclaimedAsync(string label, CancellationToken ct)
    {
        // Reads go through the REST read surface rather than a signed call, since a view costs nothing
        // and an attester sweeping hundreds of labels should not be writing to ask a question.
        using HttpResponseMessage response = await _http
            .GetAsync($"v1/names/{Uri.EscapeDataString(label)}", ct).ConfigureAwait(false);

        return response.StatusCode == HttpStatusCode.NotFound;
    }

    /// <summary>
    /// Whether this attester already reported this exact claim, so a sweep does not resubmit a
    /// transaction the contract would reject anyway.
    /// </summary>
    public Task<bool> HasAttestedAsync(string label, byte[] owner, CancellationToken ct)
    {
        // The contract rejects a duplicate from the same attester, so this is an optimisation rather
        // than a correctness requirement. Left as a cheap local guard until the read surface exposes
        // per-attester state.
        var key = label + "|" + Convert.ToHexStringLower(owner);
        return Task.FromResult(!_submitted.Add(key));
    }

    private readonly HashSet<string> _submitted = [];

    /// <summary>Reports the record. The contract counts attesters and assigns the name once enough agree.</summary>
    public async Task AttestAsync(string label, byte[] owner, string evidence, CancellationToken ct)
    {
        byte[] data = BuildCallData(label, owner, evidence);
        ulong nonce = await GetNonceAsync(ct).ConfigureAwait(false);
        UInt256 gasPrice = await GasPriceAsync(ct).ConfigureAwait(false);

        var unsigned = new Transaction
        {
            Type = TransactionType.ContractCall,
            Nonce = nonce,
            Sender = _sender,
            To = NameService(),
            Value = UInt256.Zero,
            GasLimit = GasLimit,
            GasPrice = gasPrice,
            Data = data,
            Priority = 0,
            ChainId = _chainId,
        };

        Transaction signed = Transaction.Sign(unsigned, _signingKey);

        var request = new TxRequest
        {
            Type = (byte)signed.Type,
            Nonce = signed.Nonce,
            Sender = signed.Sender.ToHexString(),
            To = signed.To.ToHexString(),
            Value = "0",
            GasLimit = signed.GasLimit,
            GasPrice = signed.GasPrice.ToString(),
            Data = Convert.ToHexStringLower(data),
            Priority = signed.Priority,
            ChainId = signed.ChainId,
            Signature = Convert.ToHexStringLower(signed.Signature.ToArray()),
            SenderPublicKey = Convert.ToHexStringLower(signed.SenderPublicKey.ToArray()),
        };

        using HttpResponseMessage response = await _http
            .PostAsJsonAsync("v1/transactions", request, AttesterJsonContext.Default.TxRequest, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"attestation refused ({(int)response.StatusCode}): {body}");
        }
    }

    /// <summary>
    /// Encodes the AttestClaim call.
    ///
    /// It goes through the SDK's own encoder rather than a local copy of the layout. A second
    /// implementation would be a second place for the encoding to drift, and the drift would surface only
    /// as attestations the chain quietly declines to decode.
    /// </summary>
    internal static byte[] BuildCallData(string label, byte[] owner, string evidence)
        => SdkContractEncoder.EncodeSdkCall(
            "AttestClaim",
            SdkContractEncoder.EncodeString(label),
            SdkContractEncoder.EncodeBytes(owner),
            SdkContractEncoder.EncodeString(evidence));

    private async Task<ulong> GetNonceAsync(CancellationToken ct)
    {
        using HttpResponseMessage response = await _http
            .GetAsync($"v1/accounts/{_sender.ToHexString()}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // An attester that has never spent has no account yet, so it starts at nonce zero. It still
            // needs a balance before its first attestation lands.
            return 0;
        }

        response.EnsureSuccessStatusCode();
        AccountInfo? account = await response.Content
            .ReadFromJsonAsync(AttesterJsonContext.Default.AccountInfo, ct).ConfigureAwait(false);
        return account?.Nonce ?? 0;
    }

    /// <summary>
    /// Pays twice the current base fee. A hardcoded price works only on a devnet whose base fee is 1,
    /// and doubling leaves room for the fee to rise between building the transaction and its inclusion.
    /// </summary>
    private async Task<UInt256> GasPriceAsync(CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage block = await _http.GetAsync("v1/blocks/latest", ct).ConfigureAwait(false);
            block.EnsureSuccessStatusCode();
            BlockInfo? latest = await block.Content
                .ReadFromJsonAsync(AttesterJsonContext.Default.BlockInfo, ct).ConfigureAwait(false);

            if (latest is not null
                && UInt256.TryParse(latest.BaseFee, out UInt256 baseFee)
                && !baseFee.IsZero)
            {
                return baseFee * new UInt256(2);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // Fall through to the floor rather than refusing to attest because one status call failed. A
            // transaction priced too low is rejected and retried next sweep, which is the cheaper failure.
        }

        return UInt256.One;
    }
}
