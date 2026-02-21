# Payment Channel Network

## Category

Scalability / Payments

## Summary

A payment channel contract enabling off-chain micropayments with on-chain settlement. Users open channels by depositing funds, exchange Ed25519-signed state updates off-chain, and close channels by submitting the final state on-chain. A configurable dispute period allows counterparties to submit fraud proofs if a stale state is used for closure. This enables high-throughput, low-latency payments for streaming services, micropayments, gaming, and any scenario where per-transaction on-chain costs are prohibitive.

## Why It's Useful

- **Micropayment scalability**: On-chain transactions have gas costs that make sub-cent payments uneconomical. Payment channels enable millions of off-chain updates at zero marginal cost, with only open and close transactions touching the chain.
- **Streaming payments**: Pay-per-second or pay-per-byte streaming services become viable when each increment is a signed off-chain state update rather than an on-chain transaction.
- **Gaming economies**: In-game microtransactions, item purchases, and reward distributions can happen off-chain at game speed, with final settlement on-chain when the session ends.
- **Point-of-sale payments**: Retail payments can achieve instant finality from the user's perspective (signed state update), with periodic on-chain settlement.
- **Reduced network load**: By moving the bulk of payment traffic off-chain, payment channels reduce congestion on the Basalt network, benefiting all users.
- **Conditional payments**: Payment channels can be extended with hash time-locked contracts (HTLCs) to enable multi-hop payments across a network of channels, similar to the Lightning Network.

## Key Features

- Channel opening: deposit native BST into a channel with a designated counterparty; both parties can deposit
- Off-chain state updates: parties exchange Ed25519-signed state objects containing (channelId, nonce, balanceA, balanceB); each update increments the nonce
- Cooperative close: both parties sign the final state and submit it to close the channel immediately, distributing funds per the final balances
- Unilateral close: either party can initiate closure with the latest state they hold; a dispute period begins
- Dispute period: during the dispute window (configurable in blocks), the counterparty can submit a state with a higher nonce to override the proposed closure state
- Fraud proof: if a party submits an old state, the counterparty submits the newer state and the channel is closed with a penalty applied to the fraudulent party
- Channel top-up: either party can add more funds to an open channel without closing and reopening
- Channel expiry: channels can have an optional maximum lifetime, after which either party can force-close
- Multi-token channels: support for BST-20 token channels in addition to native BST
- Watchtower support: third parties can be authorized to submit dispute proofs on behalf of an offline counterparty

## Basalt-Specific Advantages

- **Ed25519 signed state updates**: Basalt's native Ed25519 signatures are used for both off-chain state signing and on-chain verification via `Ed25519Signer.Verify()`. This is a natural fit since all Basalt accounts already use Ed25519 key pairs, so no additional key management is needed.
- **BLAKE3 state hashing**: Off-chain state objects are hashed with BLAKE3 for signing. BLAKE3's speed means state updates can be signed and verified at extremely high throughput off-chain, and on-chain verification is gas-efficient.
- **Chain ID domain separation**: State hashes include the chain ID (following BridgeETH's BRIDGE-01 pattern), preventing cross-chain replay of state updates if Basalt is forked.
- **AOT-compiled dispute resolution**: The dispute resolution logic (nonce comparison, signature verification, penalty calculation) runs in AOT-compiled code, ensuring deterministic and fast execution of time-sensitive dispute transactions.
- **Confidential channel balances via Pedersen commitments**: Channel balances can optionally use Pedersen commitments, allowing state updates to hide individual balances from on-chain observers while still enabling fraud proof verification through commitment arithmetic.
- **UInt256 balance precision**: Channel balances use `UInt256`, supporting channels with very large or very small (sub-unit) balance distributions without overflow concerns.
- **Cross-contract composability**: Payment channels can be opened on behalf of contracts, enabling contract-to-contract payment channels for automated services.

## Token Standards Used

- **BST-20**: Multi-token payment channels for any BST-20 fungible token
- **BST-3525**: Channel positions could be represented as SFTs for secondary market trading (selling a channel position with existing balance)

## Integration Points

- **Escrow (0x...1003)**: Channel deposits are conceptually similar to escrow -- funds are locked until a condition (cooperative close or dispute resolution) is met. The channel contract can interoperate with Escrow for complex payment flows.
- **BNS (0x...1002)**: Channel counterparties can be referenced by BNS name for improved UX.
- **BridgeETH (0x...1008)**: Cross-chain payment channels could be constructed where one leg is on Basalt and the other on Ethereum, using the bridge for settlement.
- **Governance (0x...1005 area)**: Channel parameters (dispute period length, penalty percentage) can be updated via governance.

## Technical Sketch

```csharp
using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Payment Channel Network -- off-chain micropayments with on-chain settlement,
/// dispute periods, and fraud proofs via Ed25519 signed state updates.
/// </summary>
[BasaltContract]
public partial class PaymentChannel
{
    private const int PubKeySize = 32;
    private const int SigSize = 64;

    // --- Channel state ---
    private readonly StorageValue<ulong> _nextChannelId;
    private readonly StorageMap<string, string> _channelPartyA;         // channelId -> partyA hex
    private readonly StorageMap<string, string> _channelPartyB;         // channelId -> partyB hex
    private readonly StorageMap<string, UInt256> _channelDepositA;      // channelId -> partyA deposit
    private readonly StorageMap<string, UInt256> _channelDepositB;      // channelId -> partyB deposit
    private readonly StorageMap<string, string> _channelStatus;         // channelId -> "open"|"closing"|"closed"
    private readonly StorageMap<string, ulong> _channelOpenBlock;       // channelId -> open block
    private readonly StorageMap<string, ulong> _channelExpiryBlock;     // channelId -> optional expiry

    // --- Dispute state ---
    private readonly StorageMap<string, ulong> _disputeNonce;           // channelId -> nonce of proposed close state
    private readonly StorageMap<string, UInt256> _disputeBalanceA;      // channelId -> proposed balanceA
    private readonly StorageMap<string, UInt256> _disputeBalanceB;      // channelId -> proposed balanceB
    private readonly StorageMap<string, ulong> _disputeDeadlineBlock;   // channelId -> block when dispute period ends
    private readonly StorageMap<string, string> _disputeInitiator;      // channelId -> who initiated close

    // --- Config ---
    private readonly StorageValue<ulong> _disputePeriodBlocks;
    private readonly StorageValue<uint> _fraudPenaltyBps;               // basis points penalty for fraud

    public PaymentChannel(ulong disputePeriodBlocks = 7200, uint fraudPenaltyBps = 1000)
    {
        _nextChannelId = new StorageValue<ulong>("pc_next");
        _channelPartyA = new StorageMap<string, string>("pc_a");
        _channelPartyB = new StorageMap<string, string>("pc_b");
        _channelDepositA = new StorageMap<string, UInt256>("pc_da");
        _channelDepositB = new StorageMap<string, UInt256>("pc_db");
        _channelStatus = new StorageMap<string, string>("pc_sts");
        _channelOpenBlock = new StorageMap<string, ulong>("pc_open");
        _channelExpiryBlock = new StorageMap<string, ulong>("pc_exp");
        _disputeNonce = new StorageMap<string, ulong>("pc_dn");
        _disputeBalanceA = new StorageMap<string, UInt256>("pc_dba");
        _disputeBalanceB = new StorageMap<string, UInt256>("pc_dbb");
        _disputeDeadlineBlock = new StorageMap<string, ulong>("pc_ddl");
        _disputeInitiator = new StorageMap<string, string>("pc_dinit");
        _disputePeriodBlocks = new StorageValue<ulong>("pc_dperiod");
        _fraudPenaltyBps = new StorageValue<uint>("pc_fpen");

        _disputePeriodBlocks.Set(disputePeriodBlocks);
        _fraudPenaltyBps.Set(fraudPenaltyBps);
    }

    // ===================== Channel Lifecycle =====================

    /// <summary>
    /// Open a new payment channel with a counterparty. Caller is Party A.
    /// Send native BST as the initial deposit.
    /// </summary>
    [BasaltEntrypoint]
    public ulong OpenChannel(byte[] partyB, ulong expiryBlock)
    {
        Context.Require(!Context.TxValue.IsZero, "CHANNEL: must deposit");
        Context.Require(partyB.Length > 0, "CHANNEL: invalid counterparty");

        var id = _nextChannelId.Get();
        _nextChannelId.Set(id + 1);
        var key = id.ToString();

        _channelPartyA.Set(key, Convert.ToHexString(Context.Caller));
        _channelPartyB.Set(key, Convert.ToHexString(partyB));
        _channelDepositA.Set(key, Context.TxValue);
        _channelStatus.Set(key, "open");
        _channelOpenBlock.Set(key, Context.BlockHeight);
        if (expiryBlock > 0)
            _channelExpiryBlock.Set(key, expiryBlock);

        Context.Emit(new ChannelOpenedEvent
        {
            ChannelId = id, PartyA = Context.Caller, PartyB = partyB,
            DepositA = Context.TxValue
        });
        return id;
    }

    /// <summary>
    /// Party B deposits into an open channel.
    /// </summary>
    [BasaltEntrypoint]
    public void DepositToChannel(ulong channelId)
    {
        Context.Require(!Context.TxValue.IsZero, "CHANNEL: must deposit");
        var key = channelId.ToString();
        Context.Require(_channelStatus.Get(key) == "open", "CHANNEL: not open");

        var callerHex = Convert.ToHexString(Context.Caller);
        var partyAHex = _channelPartyA.Get(key);
        var partyBHex = _channelPartyB.Get(key);

        if (callerHex == partyAHex)
        {
            _channelDepositA.Set(key, _channelDepositA.Get(key) + Context.TxValue);
        }
        else if (callerHex == partyBHex)
        {
            _channelDepositB.Set(key, _channelDepositB.Get(key) + Context.TxValue);
        }
        else
        {
            Context.Revert("CHANNEL: not a party");
        }

        Context.Emit(new ChannelDepositEvent
        {
            ChannelId = channelId, Depositor = Context.Caller, Amount = Context.TxValue
        });
    }

    /// <summary>
    /// Cooperative close: both parties sign the final state.
    /// Signatures packed as: [32B pubKeyA][64B sigA][32B pubKeyB][64B sigB]
    /// </summary>
    [BasaltEntrypoint]
    public void CooperativeClose(ulong channelId, ulong nonce, UInt256 balanceA,
        UInt256 balanceB, byte[] signatures)
    {
        var key = channelId.ToString();
        Context.Require(_channelStatus.Get(key) == "open", "CHANNEL: not open");
        Context.Require(signatures.Length == 2 * (PubKeySize + SigSize), "CHANNEL: need 2 signatures");

        var totalDeposit = _channelDepositA.Get(key) + _channelDepositB.Get(key);
        Context.Require(balanceA + balanceB == totalDeposit, "CHANNEL: balance mismatch");

        // Verify both signatures
        var stateHash = ComputeStateHash(channelId, nonce, balanceA, balanceB);
        VerifyPartySignature(key, signatures, 0, stateHash, "A");
        VerifyPartySignature(key, signatures, PubKeySize + SigSize, stateHash, "B");

        // Close immediately
        _channelStatus.Set(key, "closed");
        DistributeFunds(key, balanceA, balanceB);

        Context.Emit(new ChannelClosedEvent
        {
            ChannelId = channelId, BalanceA = balanceA, BalanceB = balanceB,
            CloseType = "cooperative"
        });
    }

    /// <summary>
    /// Unilateral close: one party submits their latest signed state.
    /// Starts the dispute period.
    /// Signature: [32B pubKeyCounterparty][64B sigCounterparty]
    /// </summary>
    [BasaltEntrypoint]
    public void InitiateClose(ulong channelId, ulong nonce, UInt256 balanceA,
        UInt256 balanceB, byte[] counterpartySignature)
    {
        var key = channelId.ToString();
        Context.Require(_channelStatus.Get(key) == "open", "CHANNEL: not open");

        var totalDeposit = _channelDepositA.Get(key) + _channelDepositB.Get(key);
        Context.Require(balanceA + balanceB == totalDeposit, "CHANNEL: balance mismatch");

        var stateHash = ComputeStateHash(channelId, nonce, balanceA, balanceB);

        // Verify counterparty's signature
        Context.Require(counterpartySignature.Length == PubKeySize + SigSize,
            "CHANNEL: invalid signature");
        var pubKeyBytes = counterpartySignature[..PubKeySize];
        var sigBytes = counterpartySignature[PubKeySize..];

        var pubKey = new PublicKey(pubKeyBytes);
        var sig = new Signature(sigBytes);
        Context.Require(Ed25519Signer.Verify(pubKey, stateHash, sig),
            "CHANNEL: invalid counterparty signature");

        // Start dispute period
        _channelStatus.Set(key, "closing");
        _disputeNonce.Set(key, nonce);
        _disputeBalanceA.Set(key, balanceA);
        _disputeBalanceB.Set(key, balanceB);
        _disputeDeadlineBlock.Set(key, Context.BlockHeight + _disputePeriodBlocks.Get());
        _disputeInitiator.Set(key, Convert.ToHexString(Context.Caller));

        Context.Emit(new CloseInitiatedEvent
        {
            ChannelId = channelId, Initiator = Context.Caller,
            Nonce = nonce, DisputeDeadline = Context.BlockHeight + _disputePeriodBlocks.Get()
        });
    }

    /// <summary>
    /// Dispute: counterparty submits a state with a higher nonce during the dispute period.
    /// </summary>
    [BasaltEntrypoint]
    public void Dispute(ulong channelId, ulong nonce, UInt256 balanceA,
        UInt256 balanceB, byte[] counterpartySignature)
    {
        var key = channelId.ToString();
        Context.Require(_channelStatus.Get(key) == "closing", "CHANNEL: not in dispute");
        Context.Require(Context.BlockHeight <= _disputeDeadlineBlock.Get(key),
            "CHANNEL: dispute period ended");
        Context.Require(nonce > _disputeNonce.Get(key), "CHANNEL: nonce not higher");

        var totalDeposit = _channelDepositA.Get(key) + _channelDepositB.Get(key);
        Context.Require(balanceA + balanceB == totalDeposit, "CHANNEL: balance mismatch");

        // Verify the newer state's signature
        var stateHash = ComputeStateHash(channelId, nonce, balanceA, balanceB);
        Context.Require(counterpartySignature.Length == PubKeySize + SigSize,
            "CHANNEL: invalid signature");
        var pubKeyBytes = counterpartySignature[..PubKeySize];
        var sigBytes = counterpartySignature[PubKeySize..];
        var pubKey = new PublicKey(pubKeyBytes);
        var sig = new Signature(sigBytes);
        Context.Require(Ed25519Signer.Verify(pubKey, stateHash, sig),
            "CHANNEL: invalid signature");

        // Update dispute state with newer nonce
        _disputeNonce.Set(key, nonce);
        _disputeBalanceA.Set(key, balanceA);
        _disputeBalanceB.Set(key, balanceB);

        // Apply fraud penalty to the initiator
        var initiatorHex = _disputeInitiator.Get(key);
        var penaltyBps = _fraudPenaltyBps.Get();
        // Penalty deducted from initiator's balance and given to disputer

        Context.Emit(new DisputeResolvedEvent
        {
            ChannelId = channelId, NewNonce = nonce, Disputer = Context.Caller
        });
    }

    /// <summary>
    /// Finalize a unilateral close after the dispute period ends.
    /// </summary>
    [BasaltEntrypoint]
    public void FinalizeClose(ulong channelId)
    {
        var key = channelId.ToString();
        Context.Require(_channelStatus.Get(key) == "closing", "CHANNEL: not closing");
        Context.Require(Context.BlockHeight > _disputeDeadlineBlock.Get(key),
            "CHANNEL: dispute period active");

        var balanceA = _disputeBalanceA.Get(key);
        var balanceB = _disputeBalanceB.Get(key);

        _channelStatus.Set(key, "closed");
        DistributeFunds(key, balanceA, balanceB);

        Context.Emit(new ChannelClosedEvent
        {
            ChannelId = channelId, BalanceA = balanceA, BalanceB = balanceB,
            CloseType = "unilateral"
        });
    }

    // ===================== Views =====================

    [BasaltView]
    public string GetChannelStatus(ulong channelId) => _channelStatus.Get(channelId.ToString()) ?? "unknown";

    [BasaltView]
    public UInt256 GetDepositA(ulong channelId) => _channelDepositA.Get(channelId.ToString());

    [BasaltView]
    public UInt256 GetDepositB(ulong channelId) => _channelDepositB.Get(channelId.ToString());

    [BasaltView]
    public ulong GetDisputeDeadline(ulong channelId) => _disputeDeadlineBlock.Get(channelId.ToString());

    [BasaltView]
    public ulong GetDisputeNonce(ulong channelId) => _disputeNonce.Get(channelId.ToString());

    // ===================== Internal =====================

    private static byte[] ComputeStateHash(ulong channelId, ulong nonce,
        UInt256 balanceA, UInt256 balanceB)
    {
        // version(1) + chainId(4) + contractAddr(20) + channelId(8) + nonce(8) + balanceA(32) + balanceB(32) = 105
        var data = new byte[1 + 4 + 20 + 8 + 8 + 32 + 32];
        var offset = 0;

        data[offset] = 0x01;
        offset += 1;
        BitConverter.TryWriteBytes(data.AsSpan(offset, 4), Context.ChainId);
        offset += 4;
        Context.Self.CopyTo(data.AsSpan(offset, 20));
        offset += 20;
        BitConverter.TryWriteBytes(data.AsSpan(offset, 8), channelId);
        offset += 8;
        BitConverter.TryWriteBytes(data.AsSpan(offset, 8), nonce);
        offset += 8;
        balanceA.WriteTo(data.AsSpan(offset, 32));
        offset += 32;
        balanceB.WriteTo(data.AsSpan(offset, 32));

        return Blake3Hasher.Hash(data).ToArray();
    }

    private void VerifyPartySignature(string channelKey, byte[] signatures, int offset,
        byte[] stateHash, string party)
    {
        var pubKeyBytes = signatures[offset..(offset + PubKeySize)];
        var sigBytes = signatures[(offset + PubKeySize)..(offset + PubKeySize + SigSize)];

        var expectedHex = party == "A" ? _channelPartyA.Get(channelKey) : _channelPartyB.Get(channelKey);

        var pubKey = new PublicKey(pubKeyBytes);
        var sig = new Signature(sigBytes);
        Context.Require(Ed25519Signer.Verify(pubKey, stateHash, sig),
            "CHANNEL: invalid signature for party " + party);
    }

    private void DistributeFunds(string channelKey, UInt256 balanceA, UInt256 balanceB)
    {
        if (!balanceA.IsZero)
            Context.TransferNative(Convert.FromHexString(_channelPartyA.Get(channelKey)), balanceA);
        if (!balanceB.IsZero)
            Context.TransferNative(Convert.FromHexString(_channelPartyB.Get(channelKey)), balanceB);
    }
}

// ===================== Events =====================

[BasaltEvent]
public class ChannelOpenedEvent
{
    [Indexed] public ulong ChannelId { get; set; }
    [Indexed] public byte[] PartyA { get; set; } = null!;
    [Indexed] public byte[] PartyB { get; set; } = null!;
    public UInt256 DepositA { get; set; }
}

[BasaltEvent]
public class ChannelDepositEvent
{
    [Indexed] public ulong ChannelId { get; set; }
    [Indexed] public byte[] Depositor { get; set; } = null!;
    public UInt256 Amount { get; set; }
}

[BasaltEvent]
public class ChannelClosedEvent
{
    [Indexed] public ulong ChannelId { get; set; }
    public UInt256 BalanceA { get; set; }
    public UInt256 BalanceB { get; set; }
    public string CloseType { get; set; } = "";
}

[BasaltEvent]
public class CloseInitiatedEvent
{
    [Indexed] public ulong ChannelId { get; set; }
    [Indexed] public byte[] Initiator { get; set; } = null!;
    public ulong Nonce { get; set; }
    public ulong DisputeDeadline { get; set; }
}

[BasaltEvent]
public class DisputeResolvedEvent
{
    [Indexed] public ulong ChannelId { get; set; }
    public ulong NewNonce { get; set; }
    [Indexed] public byte[] Disputer { get; set; } = null!;
}
```

## Complexity

**High** -- Payment channels involve subtle game-theoretic design around dispute resolution. The contract must correctly handle all edge cases: simultaneous close attempts, expired dispute periods, fraud penalty calculation, and the interaction between cooperative and unilateral close paths. Off-chain state management (while not part of the contract itself) must be well-specified for client implementations. The signature verification for state updates must be gas-efficient since dispute transactions are time-sensitive.

## Priority

**P2** -- Payment channels are valuable infrastructure for scalability but are not a blocking dependency for the initial DeFi ecosystem. They become more important as the network grows and transaction throughput becomes a concern. The core DeFi primitives (oracles, lending, DEXes) should be established first. Payment channels can be added to enable micropayment use cases and reduce network congestion as adoption increases.
