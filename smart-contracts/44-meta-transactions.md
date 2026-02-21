# Meta-Transaction Relayer / Gas Station

## Category

Infrastructure / UX

## Summary

A meta-transaction relayer contract that enables gasless user experiences by allowing users to sign transactions that relayers then submit and pay gas for. The contract verifies the user's Ed25519 signature, executes the intended action on the user's behalf, and compensates the relayer with a fee paid by the user or the sponsoring dApp. This follows the EIP-2771 trusted forwarder pattern, adapted for Basalt's Ed25519 signature scheme, and is essential for onboarding new users who do not yet hold native BST for gas.

## Why It's Useful

- **Zero-friction onboarding**: New users should not need to acquire native BST before they can interact with dApps. Meta-transactions allow users to start using the chain immediately with a sponsored experience.
- **dApp-sponsored gas**: dApps can sponsor gas costs for their users, absorbing the cost as a customer acquisition expense. This is standard practice in Web2 and expected in Web3.
- **Mobile-first UX**: Mobile wallets can sign transactions without managing gas balances, with a background relayer handling submission. This dramatically simplifies the user experience.
- **Batch operations**: Relayers can batch multiple meta-transactions into a single on-chain transaction, reducing per-user gas costs and improving network efficiency.
- **Relayer competition**: Multiple relayers can compete to submit transactions, ensuring reliability and preventing single points of failure.
- **Token-denominated fees**: Users can pay relayer fees in BST-20 tokens rather than native BST, enabling flexible payment options.

## Key Features

- User signs a meta-transaction containing (from, to, value, data, nonce, deadline, maxFee) with their Ed25519 private key
- Relayer submits the meta-transaction to the forwarder contract, which verifies the user's signature and executes the action
- Per-user nonce tracking prevents replay attacks
- Deadline field prevents stale meta-transactions from being submitted long after signing
- MaxFee cap ensures the user controls the maximum gas cost they are willing to bear
- Relayer earns a fee from the user's balance or from the sponsoring dApp's pre-funded balance
- dApp sponsorship: dApps can pre-fund gas for their users by depositing into the contract and registering as a sponsor
- Relayer registration: relayers must register (optionally with a stake) to prevent spam and ensure quality of service
- Batch execution: multiple meta-transactions can be executed in a single call
- Domain separation: meta-transaction hashes include chain ID, forwarder contract address, and version to prevent cross-chain and cross-contract replay
- Trusted forwarder pattern: target contracts recognize the forwarder as a trusted caller and extract the original sender from the appended calldata

## Basalt-Specific Advantages

- **Native Ed25519 meta-transaction signing**: Users sign meta-transactions with the same Ed25519 key pair they use for regular Basalt transactions. There is no separate signing scheme or key derivation required. Verification uses `Ed25519Signer.Verify()` with zero overhead.
- **BLAKE3 domain-separated hashing**: Meta-transaction hashes use BLAKE3 with chain ID, contract address, and version prefix (following the BridgeETH pattern), providing fast and secure domain separation.
- **AOT-compiled signature verification**: The critical path (verify signature, check nonce, execute) runs in AOT-compiled code, ensuring low and predictable gas costs for relayer operations.
- **ZK identity for Sybil resistance**: dApp sponsors can require that users hold a valid ZK identity credential (via SchemaRegistry/IssuerRegistry) before sponsoring their gas, preventing Sybil attacks on gas sponsorship programs.
- **Cross-contract execution**: The forwarder uses `Context.CallContract()` to execute the user's intended action on the target contract, with automatic reentrancy protection provided by the runtime.
- **Confidential relay fees via Pedersen commitments**: Relay fees can be committed using Pedersen commitments, hiding the fee amount from on-chain observers while allowing the relayer to verify they received the correct payment.
- **UInt256 fee precision**: Fee amounts use `UInt256`, supporting both micropayment fees (sub-unit precision) and large-value meta-transactions.

## Token Standards Used

- **BST-20**: Relayer fees can be paid in BST-20 tokens. dApp sponsors can deposit BST-20 tokens to fund user gas.
- **BST-721**: Meta-transactions can be used to enable gasless NFT minting, transfers, and approvals for new users.

## Integration Points

- **All system contracts**: The meta-transaction forwarder is a universal wrapper -- any contract call can be executed through it. This includes interactions with Governance, StakingPool, Escrow, BNS, BridgeETH, and all token contracts.
- **SchemaRegistry (0x...1006)**: ZK identity verification for gas sponsorship eligibility.
- **IssuerRegistry (0x...1007)**: Verifiable credentials for relayer certification.
- **BNS (0x...1002)**: Gasless BNS name registration for new users.
- **Governance (0x...1005 area)**: Gasless governance voting, enabling broader participation.

## Technical Sketch

```csharp
using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Meta-Transaction Forwarder / Gas Station -- enables gasless UX by allowing users
/// to sign transactions that relayers submit on their behalf.
/// EIP-2771 trusted forwarder pattern adapted for Basalt's Ed25519 signatures.
/// </summary>
[BasaltContract]
public partial class MetaTransactionForwarder
{
    private const int PubKeySize = 32;
    private const int SigSize = 64;
    private const int AddressSize = 20;

    // --- Nonce tracking (replay protection) ---
    private readonly StorageMap<string, ulong> _nonces;                 // senderHex -> next expected nonce

    // --- Relayer management ---
    private readonly StorageMap<string, bool> _registeredRelayers;      // relayerHex -> registered
    private readonly StorageMap<string, UInt256> _relayerStakes;        // relayerHex -> stake
    private readonly StorageValue<UInt256> _minRelayerStake;
    private readonly StorageValue<uint> _relayerCount;

    // --- dApp sponsorship ---
    private readonly StorageMap<string, UInt256> _sponsorBalances;      // sponsorHex -> pre-funded balance
    private readonly StorageMap<string, string> _sponsorAllowList;      // "sponsorHex:targetHex" -> "allowed"

    // --- Fee tracking ---
    private readonly StorageMap<string, UInt256> _relayerEarnings;      // relayerHex -> total earned
    private readonly StorageValue<UInt256> _totalFeesCollected;

    // --- Config ---
    private readonly StorageValue<ulong> _maxMetaTxAge;                 // max blocks a meta-tx is valid
    private readonly StorageMap<string, string> _admin;

    public MetaTransactionForwarder(UInt256 minRelayerStake = default, ulong maxMetaTxAge = 7200)
    {
        _nonces = new StorageMap<string, ulong>("mtf_nonce");
        _registeredRelayers = new StorageMap<string, bool>("mtf_rel");
        _relayerStakes = new StorageMap<string, UInt256>("mtf_rstk");
        _minRelayerStake = new StorageValue<UInt256>("mtf_minstk");
        _relayerCount = new StorageValue<uint>("mtf_rcnt");
        _sponsorBalances = new StorageMap<string, UInt256>("mtf_sbal");
        _sponsorAllowList = new StorageMap<string, string>("mtf_sallow");
        _relayerEarnings = new StorageMap<string, UInt256>("mtf_rearn");
        _totalFeesCollected = new StorageValue<UInt256>("mtf_tfee");
        _maxMetaTxAge = new StorageValue<ulong>("mtf_maxage");
        _admin = new StorageMap<string, string>("mtf_admin");

        if (minRelayerStake.IsZero)
            minRelayerStake = new UInt256(1000);
        _minRelayerStake.Set(minRelayerStake);
        _maxMetaTxAge.Set(maxMetaTxAge);
        _admin.Set("admin", Convert.ToHexString(Context.Caller));
    }

    // ===================== Relayer Management =====================

    /// <summary>
    /// Register as a relayer by staking the minimum amount.
    /// </summary>
    [BasaltEntrypoint]
    public void RegisterRelayer()
    {
        Context.Require(Context.TxValue >= _minRelayerStake.Get(), "MTF: insufficient stake");
        var hex = Convert.ToHexString(Context.Caller);
        Context.Require(!_registeredRelayers.Get(hex), "MTF: already registered");

        _registeredRelayers.Set(hex, true);
        _relayerStakes.Set(hex, Context.TxValue);
        _relayerCount.Set(_relayerCount.Get() + 1);

        Context.Emit(new RelayerRegisteredEvent
        {
            Relayer = Context.Caller, Stake = Context.TxValue
        });
    }

    /// <summary>
    /// Unregister as a relayer and withdraw stake.
    /// </summary>
    [BasaltEntrypoint]
    public void UnregisterRelayer()
    {
        var hex = Convert.ToHexString(Context.Caller);
        Context.Require(_registeredRelayers.Get(hex), "MTF: not registered");

        var stake = _relayerStakes.Get(hex);
        _registeredRelayers.Set(hex, false);
        _relayerStakes.Set(hex, UInt256.Zero);
        _relayerCount.Set(_relayerCount.Get() - 1);

        Context.TransferNative(Context.Caller, stake);

        Context.Emit(new RelayerUnregisteredEvent { Relayer = Context.Caller });
    }

    // ===================== dApp Sponsorship =====================

    /// <summary>
    /// Deposit funds to sponsor gas for users interacting with specific target contracts.
    /// </summary>
    [BasaltEntrypoint]
    public void SponsorDeposit()
    {
        Context.Require(!Context.TxValue.IsZero, "MTF: must send value");
        var hex = Convert.ToHexString(Context.Caller);
        _sponsorBalances.Set(hex, _sponsorBalances.Get(hex) + Context.TxValue);

        Context.Emit(new SponsorDepositEvent
        {
            Sponsor = Context.Caller, Amount = Context.TxValue
        });
    }

    /// <summary>
    /// Allow a specific target contract for sponsored transactions.
    /// </summary>
    [BasaltEntrypoint]
    public void AllowTarget(byte[] targetContract)
    {
        var sponsorHex = Convert.ToHexString(Context.Caller);
        var targetHex = Convert.ToHexString(targetContract);
        _sponsorAllowList.Set(sponsorHex + ":" + targetHex, "allowed");
    }

    /// <summary>
    /// Withdraw unused sponsor funds.
    /// </summary>
    [BasaltEntrypoint]
    public void SponsorWithdraw(UInt256 amount)
    {
        var hex = Convert.ToHexString(Context.Caller);
        var balance = _sponsorBalances.Get(hex);
        Context.Require(balance >= amount, "MTF: insufficient sponsor balance");

        _sponsorBalances.Set(hex, balance - amount);
        Context.TransferNative(Context.Caller, amount);
    }

    // ===================== Execute Meta-Transaction =====================

    /// <summary>
    /// Execute a meta-transaction on behalf of a user.
    /// The relayer (Context.Caller) pays gas; the user's signature authorizes the action.
    ///
    /// Parameters:
    ///   userPublicKey: 32-byte Ed25519 public key of the user
    ///   targetContract: address of the contract to call
    ///   methodName: method to invoke on the target
    ///   nonce: user's meta-tx nonce (must match expected)
    ///   deadline: block number after which this meta-tx is invalid
    ///   maxFee: maximum fee the user is willing to pay the relayer
    ///   signature: 64-byte Ed25519 signature over the meta-tx hash
    /// </summary>
    [BasaltEntrypoint]
    public void Execute(byte[] userPublicKey, byte[] targetContract, string methodName,
        ulong nonce, ulong deadline, UInt256 maxFee, byte[] signature)
    {
        RequireRegisteredRelayer();

        // Validate deadline
        Context.Require(Context.BlockHeight <= deadline, "MTF: meta-tx expired");

        // Validate nonce
        var userHex = Convert.ToHexString(userPublicKey);
        var expectedNonce = _nonces.Get(userHex);
        Context.Require(nonce == expectedNonce, "MTF: invalid nonce");

        // Compute meta-tx hash
        var metaTxHash = ComputeMetaTxHash(
            userPublicKey, targetContract, methodName, nonce, deadline, maxFee);

        // Verify user's Ed25519 signature
        var pubKey = new PublicKey(userPublicKey);
        var sig = new Signature(signature);
        Context.Require(Ed25519Signer.Verify(pubKey, metaTxHash, sig),
            "MTF: invalid user signature");

        // Increment nonce
        _nonces.Set(userHex, expectedNonce + 1);

        // Execute the target call
        Context.CallContract(targetContract, methodName);

        // Charge fee to user or sponsor
        var relayerHex = Convert.ToHexString(Context.Caller);
        _relayerEarnings.Set(relayerHex, _relayerEarnings.Get(relayerHex) + maxFee);
        _totalFeesCollected.Set(_totalFeesCollected.Get() + maxFee);

        Context.Emit(new MetaTxExecutedEvent
        {
            User = userPublicKey, Relayer = Context.Caller,
            Target = targetContract, MethodName = methodName,
            Nonce = nonce, Fee = maxFee
        });
    }

    /// <summary>
    /// Execute a sponsored meta-transaction where the dApp pays the relayer fee.
    /// </summary>
    [BasaltEntrypoint]
    public void ExecuteSponsored(byte[] userPublicKey, byte[] targetContract, string methodName,
        ulong nonce, ulong deadline, byte[] sponsor, UInt256 maxFee, byte[] signature)
    {
        RequireRegisteredRelayer();
        Context.Require(Context.BlockHeight <= deadline, "MTF: meta-tx expired");

        var userHex = Convert.ToHexString(userPublicKey);
        var expectedNonce = _nonces.Get(userHex);
        Context.Require(nonce == expectedNonce, "MTF: invalid nonce");

        // Verify sponsor allows this target
        var sponsorHex = Convert.ToHexString(sponsor);
        var targetHex = Convert.ToHexString(targetContract);
        Context.Require(
            _sponsorAllowList.Get(sponsorHex + ":" + targetHex) == "allowed",
            "MTF: target not sponsored");

        // Check sponsor balance
        var sponsorBalance = _sponsorBalances.Get(sponsorHex);
        Context.Require(sponsorBalance >= maxFee, "MTF: insufficient sponsor balance");

        // Compute and verify meta-tx hash
        var metaTxHash = ComputeMetaTxHash(
            userPublicKey, targetContract, methodName, nonce, deadline, maxFee);
        var pubKey = new PublicKey(userPublicKey);
        var sig = new Signature(signature);
        Context.Require(Ed25519Signer.Verify(pubKey, metaTxHash, sig),
            "MTF: invalid user signature");

        // Increment nonce
        _nonces.Set(userHex, expectedNonce + 1);

        // Execute
        Context.CallContract(targetContract, methodName);

        // Deduct from sponsor, credit relayer
        _sponsorBalances.Set(sponsorHex, sponsorBalance - maxFee);
        var relayerHex = Convert.ToHexString(Context.Caller);
        _relayerEarnings.Set(relayerHex, _relayerEarnings.Get(relayerHex) + maxFee);

        Context.Emit(new SponsoredMetaTxExecutedEvent
        {
            User = userPublicKey, Sponsor = sponsor, Relayer = Context.Caller,
            Target = targetContract, Nonce = nonce, Fee = maxFee
        });
    }

    /// <summary>
    /// Relayer claims accumulated earnings.
    /// </summary>
    [BasaltEntrypoint]
    public void ClaimEarnings()
    {
        var hex = Convert.ToHexString(Context.Caller);
        var earnings = _relayerEarnings.Get(hex);
        Context.Require(!earnings.IsZero, "MTF: no earnings");

        _relayerEarnings.Set(hex, UInt256.Zero);
        Context.TransferNative(Context.Caller, earnings);

        Context.Emit(new EarningsClaimedEvent { Relayer = Context.Caller, Amount = earnings });
    }

    // ===================== Views =====================

    [BasaltView]
    public ulong GetNonce(byte[] userPublicKey) => _nonces.Get(Convert.ToHexString(userPublicKey));

    [BasaltView]
    public bool IsRelayer(byte[] relayer) => _registeredRelayers.Get(Convert.ToHexString(relayer));

    [BasaltView]
    public UInt256 GetSponsorBalance(byte[] sponsor) => _sponsorBalances.Get(Convert.ToHexString(sponsor));

    [BasaltView]
    public UInt256 GetRelayerEarnings(byte[] relayer) => _relayerEarnings.Get(Convert.ToHexString(relayer));

    [BasaltView]
    public uint GetRelayerCount() => _relayerCount.Get();

    // ===================== Internal =====================

    private void RequireRegisteredRelayer()
    {
        Context.Require(
            _registeredRelayers.Get(Convert.ToHexString(Context.Caller)),
            "MTF: not a registered relayer");
    }

    private static byte[] ComputeMetaTxHash(byte[] userPublicKey, byte[] targetContract,
        string methodName, ulong nonce, ulong deadline, UInt256 maxFee)
    {
        // Hash: version(1) + chainId(4) + forwarder(20) + userPubKey(32) + target(20) +
        //       methodHash(32) + nonce(8) + deadline(8) + maxFee(32) = 157 bytes
        var methodBytes = System.Text.Encoding.UTF8.GetBytes(methodName);
        var methodHash = Blake3Hasher.Hash(methodBytes).ToArray();

        var data = new byte[1 + 4 + 20 + 32 + 20 + 32 + 8 + 8 + 32];
        var offset = 0;

        data[offset] = 0x01; // version
        offset += 1;
        BitConverter.TryWriteBytes(data.AsSpan(offset, 4), Context.ChainId);
        offset += 4;
        Context.Self.CopyTo(data.AsSpan(offset, 20));
        offset += 20;
        userPublicKey.CopyTo(data.AsSpan(offset, 32));
        offset += 32;
        targetContract.CopyTo(data.AsSpan(offset, 20));
        offset += 20;
        methodHash.CopyTo(data.AsSpan(offset, 32));
        offset += 32;
        BitConverter.TryWriteBytes(data.AsSpan(offset, 8), nonce);
        offset += 8;
        BitConverter.TryWriteBytes(data.AsSpan(offset, 8), deadline);
        offset += 8;
        maxFee.WriteTo(data.AsSpan(offset, 32));

        return Blake3Hasher.Hash(data).ToArray();
    }
}

// ===================== Events =====================

[BasaltEvent]
public class MetaTxExecutedEvent
{
    [Indexed] public byte[] User { get; set; } = null!;
    [Indexed] public byte[] Relayer { get; set; } = null!;
    public byte[] Target { get; set; } = null!;
    public string MethodName { get; set; } = "";
    public ulong Nonce { get; set; }
    public UInt256 Fee { get; set; }
}

[BasaltEvent]
public class SponsoredMetaTxExecutedEvent
{
    [Indexed] public byte[] User { get; set; } = null!;
    [Indexed] public byte[] Sponsor { get; set; } = null!;
    [Indexed] public byte[] Relayer { get; set; } = null!;
    public byte[] Target { get; set; } = null!;
    public ulong Nonce { get; set; }
    public UInt256 Fee { get; set; }
}

[BasaltEvent]
public class RelayerRegisteredEvent
{
    [Indexed] public byte[] Relayer { get; set; } = null!;
    public UInt256 Stake { get; set; }
}

[BasaltEvent]
public class RelayerUnregisteredEvent
{
    [Indexed] public byte[] Relayer { get; set; } = null!;
}

[BasaltEvent]
public class SponsorDepositEvent
{
    [Indexed] public byte[] Sponsor { get; set; } = null!;
    public UInt256 Amount { get; set; }
}

[BasaltEvent]
public class EarningsClaimedEvent
{
    [Indexed] public byte[] Relayer { get; set; } = null!;
    public UInt256 Amount { get; set; }
}
```

## Complexity

**Medium** -- The core logic (verify signature, check nonce, execute call, charge fee) is straightforward. The main complexity lies in the meta-transaction hash construction (which must be byte-for-byte reproducible by off-chain signers) and the sponsored execution flow (sponsor balance management, allow-list checking). The trusted forwarder pattern requires target contracts to understand and extract the original sender, which is a protocol-level concern rather than a contract-level one.

## Priority

**P1** -- Meta-transactions are critical for user onboarding and dApp adoption. New users cannot interact with the chain without gas, creating a chicken-and-egg problem. A faucet helps for testnet/devnet, but a meta-transaction forwarder is the production solution. This should be deployed early to enable dApps to offer gasless experiences from day one.
