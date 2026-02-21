using System.Text;
using System.Text.Json;
using Basalt.Codec;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution;
using Basalt.Sdk.Contracts;
using Basalt.Sdk.Wallet.Contracts;
using Basalt.Sdk.Wallet.HdWallet;

// Generate test vectors for TypeScript SDK cross-validation

var vectors = new Dictionary<string, object>();

// 1. Known Ed25519 key pair
var fixedPrivateKey = new byte[32];
fixedPrivateKey[0] = 0x01; // deterministic key
var publicKey = Ed25519Signer.GetPublicKey(fixedPrivateKey);
var address = KeccakHasher.DeriveAddress(publicKey);

vectors["ed25519"] = new
{
    privateKey = Convert.ToHexString(fixedPrivateKey),
    publicKey = Convert.ToHexString(publicKey.ToArray()),
    address = address.ToString(),
};

// 2. Ed25519 sign + verify
var message = Encoding.UTF8.GetBytes("Hello, Basalt!");
var signature = Ed25519Signer.Sign(fixedPrivateKey, message);
vectors["ed25519_sign"] = new
{
    message = Convert.ToHexString(message),
    signature = Convert.ToHexString(signature.ToArray()),
};

// 3. BLAKE3 hash
var blake3Input = Encoding.UTF8.GetBytes("test data for blake3");
var blake3Hash = Blake3Hasher.Hash(blake3Input);
vectors["blake3"] = new
{
    input = Convert.ToHexString(blake3Input),
    hash = Convert.ToHexString(blake3Hash.ToArray()),
};

// 4. Keccak-256 hash — also verify against known test vector
var keccakAbc = KeccakHasher.Hash(Encoding.UTF8.GetBytes("abc"));
Console.Error.WriteLine($"KeccakHasher(abc) = {Convert.ToHexString(keccakAbc)}");
Console.Error.WriteLine($"Expected          = 4E03657AEA45A94FC7D47BA826C8D667C0D1E6E33A64A036EC44F58FA12D6C45");
var keccakInput = Encoding.UTF8.GetBytes("test data for keccak");
var keccakHash = KeccakHasher.Hash(keccakInput);
vectors["keccak256"] = new
{
    input = Convert.ToHexString(keccakInput),
    hash = Convert.ToHexString(keccakHash),
};

// 5. Address derivation from known public key
vectors["address_derivation"] = new
{
    publicKey = Convert.ToHexString(publicKey.ToArray()),
    address = address.ToString(),
    addressHex = Convert.ToHexString(address.ToArray()),
};

// 6. UInt256 serialization
var uint256Value = new UInt256(1_000_000_000_000_000_000UL); // 1e18
var uint256Bytes = uint256Value.ToArray();
var uint256BytesBE = uint256Value.ToArray(isBigEndian: true);
vectors["uint256"] = new
{
    decimalValue = "1000000000000000000",
    bytesLE = Convert.ToHexString(uint256Bytes),
    bytesBE = Convert.ToHexString(uint256BytesBE),
};

// 7. LEB128 / VarInt encoding
var varIntTestValues = new ulong[] { 0, 127, 128, 16384, 2097152, 268435456, ulong.MaxValue };
var varIntResults = new List<object>();
foreach (var v in varIntTestValues)
{
    Span<byte> buf = stackalloc byte[16];
    var writer = new BasaltWriter(buf);
    writer.WriteVarInt(v);
    varIntResults.Add(new { value = v.ToString(), hex = Convert.ToHexString(buf[..writer.Position]) });
}
vectors["varint"] = varIntResults;

// 8. BasaltWriter string encoding
Span<byte> strBuf = stackalloc byte[256];
var strWriter = new BasaltWriter(strBuf);
strWriter.WriteString("Hello");
vectors["writer_string"] = new
{
    input = "Hello",
    hex = Convert.ToHexString(strBuf[..strWriter.Position]),
};

// 9. BasaltWriter bytes encoding
Span<byte> bytesBuf = stackalloc byte[256];
var bytesWriter = new BasaltWriter(bytesBuf);
bytesWriter.WriteBytes(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
vectors["writer_bytes"] = new
{
    input = "DEADBEEF",
    hex = Convert.ToHexString(bytesBuf[..bytesWriter.Position]),
};

// 10. Transaction signing payload
var tx = new Transaction
{
    Type = TransactionType.Transfer,
    Nonce = 42,
    Sender = address,
    To = Address.Zero,
    Value = new UInt256(1_000_000_000_000_000_000UL), // 1 BSLT
    GasLimit = 21_000,
    GasPrice = new UInt256(1_000_000_000UL), // 1 gwei
    MaxFeePerGas = UInt256.Zero,
    MaxPriorityFeePerGas = UInt256.Zero,
    Data = [],
    Priority = 0,
    ChainId = 31337,
};

Span<byte> payloadBuf = stackalloc byte[tx.GetSigningPayloadSize()];
tx.WriteSigningPayload(payloadBuf);
var txHash = Blake3Hasher.Hash(payloadBuf);

vectors["transaction"] = new
{
    type = (byte)tx.Type,
    nonce = tx.Nonce.ToString(),
    sender = tx.Sender.ToString(),
    to = tx.To.ToString(),
    value = "1000000000000000000",
    gasLimit = tx.GasLimit.ToString(),
    gasPrice = "1000000000",
    chainId = tx.ChainId,
    payloadHex = Convert.ToHexString(payloadBuf.ToArray()),
    payloadSize = tx.GetSigningPayloadSize(),
    hashHex = Convert.ToHexString(txHash.ToArray()),
};

// 11. Sign the transaction
var signedTx = Transaction.Sign(tx, fixedPrivateKey);
vectors["transaction_signed"] = new
{
    signatureHex = Convert.ToHexString(signedTx.Signature.ToArray()),
    senderPublicKeyHex = Convert.ToHexString(signedTx.SenderPublicKey.ToArray()),
};

// 12. FNV-1a selectors
var methodNames = new[] { "Transfer", "Approve", "BalanceOf", "TotalSupply", "CreateProposal", "Vote" };
var fnvSelectors = new List<object>();
foreach (var name in methodNames)
{
    var sel = SelectorHelper.ComputeSelector(name);
    var selBytes = SelectorHelper.ComputeSelectorBytes(name);
    fnvSelectors.Add(new
    {
        method = name,
        selectorUint = sel,
        selectorHex = Convert.ToHexString(selBytes),
    });
}
vectors["fnv1a_selectors"] = fnvSelectors;

// 13. BLAKE3 selectors (ABI encoder)
var blake3Selectors = new List<object>();
foreach (var name in methodNames)
{
    var sel = AbiEncoder.ComputeSelector(name);
    blake3Selectors.Add(new
    {
        method = name,
        selectorHex = Convert.ToHexString(sel),
    });
}
vectors["blake3_selectors"] = blake3Selectors;

// 14. SDK encoder
var sdkCallData = SdkContractEncoder.EncodeSdkCall("Transfer",
    SdkContractEncoder.EncodeBytes(new byte[20]), // to address
    SdkContractEncoder.EncodeUInt64(1_000_000UL)); // amount
vectors["sdk_call"] = new
{
    method = "Transfer",
    hex = Convert.ToHexString(sdkCallData),
};

// 15. ABI encoder
var abiCallData = AbiEncoder.EncodeCall("transfer",
    AbiEncoder.EncodeAddress(Address.Zero),
    AbiEncoder.EncodeUInt256(new UInt256(1_000_000UL)));
vectors["abi_call"] = new
{
    method = "transfer",
    hex = Convert.ToHexString(abiCallData),
};

// 16. HD wallet — known mnemonic
var knownMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
var seed = Mnemonic.ToSeed(knownMnemonic);
var path0 = DerivationPath.Basalt(0);
var derivedKey0 = HdKeyDerivation.DerivePath(seed, path0);
var derivedPub0 = Ed25519Signer.GetPublicKey(derivedKey0);
var derivedAddr0 = KeccakHasher.DeriveAddress(derivedPub0);

var path1 = DerivationPath.Basalt(1);
var derivedKey1 = HdKeyDerivation.DerivePath(seed, path1);
var derivedPub1 = Ed25519Signer.GetPublicKey(derivedKey1);
var derivedAddr1 = KeccakHasher.DeriveAddress(derivedPub1);

vectors["hd_wallet"] = new
{
    mnemonic = knownMnemonic,
    seedHex = Convert.ToHexString(seed),
    accounts = new[]
    {
        new
        {
            index = 0,
            path = path0.Path,
            privateKeyHex = Convert.ToHexString(derivedKey0),
            publicKeyHex = Convert.ToHexString(derivedPub0.ToArray()),
            address = derivedAddr0.ToString(),
        },
        new
        {
            index = 1,
            path = path1.Path,
            privateKeyHex = Convert.ToHexString(derivedKey1),
            publicKeyHex = Convert.ToHexString(derivedPub1.ToArray()),
            address = derivedAddr1.ToString(),
        },
    },
};

// Output as JSON
var json = JsonSerializer.Serialize(vectors, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(json);
