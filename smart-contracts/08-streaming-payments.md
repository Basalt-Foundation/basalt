# Streaming Payments (Sablier-style)

## Category

Decentralized Finance (DeFi) -- Payments and Vesting

## Summary

A streaming payments protocol enabling continuous, per-block token transfers from a sender to a receiver. Senders create payment streams that linearly (or in stepped intervals) unlock tokens over a defined duration. Receivers can claim accrued tokens at any time. The protocol supports cancellable and non-cancellable streams, making it suitable for salaries, vesting schedules, subscriptions, grants, and recurring payments.

## Why It's Useful

- **Real-Time Payments**: Instead of lump-sum monthly payments, workers receive earnings continuously in real time, improving cash flow and eliminating trust requirements for future payments.
- **Token Vesting**: Projects can vest tokens to team members, advisors, and investors using trustless on-chain streams with configurable cliff periods and linear unlock schedules.
- **Subscription Services**: SaaS providers and content creators can implement pay-per-second subscription models where users stop paying immediately upon cancellation.
- **Grant Disbursement**: DAOs and foundations can stream grants to funded projects, with the ability to cancel if milestones are not met, avoiding upfront lump-sum risk.
- **Payroll Automation**: Organizations can set up automated payroll streams, reducing administrative overhead and providing employees with instant access to earned wages.
- **Escrow Alternative**: Streams serve as a time-based escrow mechanism where funds are gradually released, providing assurance to both parties.

## Key Features

- **Linear Streams**: Tokens unlock at a constant rate per block over the stream duration. At any point, the receiver can claim their accrued portion.
- **Stepped (Cliff) Streams**: No tokens are claimable until a cliff block is reached, after which linear streaming begins. Useful for vesting with cliff periods.
- **Cancellable Streams**: Sender can cancel a stream at any time. Accrued (unlocked) tokens go to the receiver; unstreamed tokens are refunded to the sender.
- **Non-Cancellable Streams**: Once created, the stream cannot be stopped. Guarantees the receiver will receive all tokens over the duration. Useful for irrevocable grants.
- **Partial Claims**: Receivers can claim any portion of their accrued balance at any time without affecting the stream's ongoing behavior.
- **Multiple Concurrent Streams**: A sender can have many active streams to different receivers, and a receiver can receive from many senders simultaneously.
- **Stream Transfer**: Receivers can transfer their stream position to another address (useful for selling vesting positions on secondary markets).
- **Batch Stream Creation**: Create multiple streams in a single transaction for efficiency (e.g., company payroll for 50 employees).
- **Top-Up Streams**: Senders can add additional tokens to extend or increase an existing stream.
- **Stream Metadata**: Optional metadata field for attaching context (invoice reference, employee ID via BNS name, purpose).

## Basalt-Specific Advantages

- **Per-Block Granularity with Fast Blocks**: Basalt's block time provides fine-grained streaming resolution. Combined with AOT-compiled execution, claim calculations (blocks elapsed * rate) are extremely cheap to compute, enabling frequent claims without excessive gas cost.
- **BST-3525 SFT Stream Positions**: Each stream is represented as a BST-3525 semi-fungible token with metadata encoding the stream parameters (sender, rate, start, end, cliff, claimed amount). This enables streams to be traded on secondary markets -- an investor can sell their vesting position before it fully unlocks.
- **ZK Compliance for Payroll Privacy**: Salary streams can use ZK compliance proofs to verify employment eligibility without revealing compensation amounts on-chain. Combined with Pedersen commitments on stream amounts, payroll is fully confidential while remaining verifiable.
- **Confidential Stream Amounts**: Pedersen commitments hide the stream amount and rate, ensuring that compensation details (salaries, grant amounts) are not publicly visible on the blockchain while still allowing the receiver to claim their correct accrual.
- **BNS-Addressed Streams**: Streams can be created using BNS names instead of raw addresses (e.g., stream to `alice.basalt` instead of `0x...`), improving UX for payroll and grant disbursement.
- **Ed25519 Batch Signatures**: Batch stream creation for large payrolls uses Ed25519 signed batch authorization, enabling efficient creation of hundreds of streams in a single transaction.

## Token Standards Used

- **BST-20**: All streamed tokens are BST-20. Native BST (via WBSLT) can also be streamed.
- **BST-3525 (SFT)**: Stream positions represented as semi-fungible tokens with rich metadata, enabling transferability and secondary market trading.

## Integration Points

- **BNS (0x0101)**: Stream recipients can be specified by BNS name for human-readable addressing.
- **Governance (0x0102)**: Controls protocol fee parameters and emergency pause.
- **Escrow (0x0103)**: Locked stream funds are held in escrow until claimed or refunded.
- **WBSLT (0x0100)**: Native BST is wrapped to WBSLT for streaming, then unwrapped on claim.
- **SchemaRegistry / IssuerRegistry**: ZK compliance for confidential payroll streams.

## Technical Sketch

```csharp
// ============================================================
// StreamingPayments -- Continuous token streaming
// ============================================================

public enum StreamStatus : byte
{
    Active = 0,
    Completed = 1,
    Canceled = 2
}

[BasaltContract(TypeId = 0x0270)]
public partial class StreamingPayments : SdkContract
{
    // --- Storage ---

    // streamId => Stream
    private StorageMap<ulong, Stream> _streams;
    private StorageValue<ulong> _nextStreamId;

    // Protocol fee configuration
    private StorageValue<uint> _protocolFeeBps;
    private StorageValue<Address> _feeRecipient;

    // Sender index: sender => list of streamIds (for enumeration)
    private StorageMap<Address, ulong> _senderStreamCount;
    private StorageMap<Hash256, ulong> _senderStreamIndex; // sender + index => streamId

    // Receiver index: receiver => list of streamIds
    private StorageMap<Address, ulong> _receiverStreamCount;
    private StorageMap<Hash256, ulong> _receiverStreamIndex;

    // --- Structs ---

    public struct Stream
    {
        public ulong StreamId;
        public Address Sender;
        public Address Receiver;
        public Address Token;
        public UInt256 TotalAmount;
        public UInt256 ClaimedAmount;
        public ulong StartBlock;
        public ulong EndBlock;
        public ulong CliffBlock;        // 0 = no cliff
        public bool Cancelable;
        public StreamStatus Status;
        public byte[] Metadata;         // optional context
    }

    // --- Stream Creation ---

    /// <summary>
    /// Create a new linear payment stream. Locks tokens for the full
    /// duration. The receiver can claim accrued tokens at any time.
    /// </summary>
    public ulong CreateStream(
        Address receiver,
        Address token,
        UInt256 totalAmount,
        ulong startBlock,
        ulong endBlock,
        ulong cliffBlock,
        bool cancelable,
        byte[] metadata)
    {
        Require(receiver != Address.Zero, "ZERO_RECEIVER");
        Require(receiver != Context.Sender, "SELF_STREAM");
        Require(totalAmount > UInt256.Zero, "ZERO_AMOUNT");
        Require(endBlock > startBlock, "INVALID_DURATION");
        Require(cliffBlock == 0 || (cliffBlock >= startBlock && cliffBlock <= endBlock),
                "INVALID_CLIFF");

        // Transfer tokens from sender to contract (locked for duration)
        TransferTokenIn(token, Context.Sender, totalAmount);

        var streamId = _nextStreamId.Get();
        _nextStreamId.Set(streamId + 1);

        var stream = new Stream
        {
            StreamId = streamId,
            Sender = Context.Sender,
            Receiver = receiver,
            Token = token,
            TotalAmount = totalAmount,
            ClaimedAmount = UInt256.Zero,
            StartBlock = startBlock,
            EndBlock = endBlock,
            CliffBlock = cliffBlock,
            Cancelable = cancelable,
            Status = StreamStatus.Active,
            Metadata = metadata
        };

        _streams.Set(streamId, stream);

        // Update indices
        IndexStream(Context.Sender, receiver, streamId);

        EmitEvent("StreamCreated", streamId, Context.Sender, receiver,
                  token, totalAmount, startBlock, endBlock);
        return streamId;
    }

    /// <summary>
    /// Create multiple streams in a single transaction (batch payroll).
    /// </summary>
    public ulong[] CreateBatchStreams(
        Address[] receivers,
        Address token,
        UInt256[] amounts,
        ulong startBlock,
        ulong endBlock,
        ulong cliffBlock,
        bool cancelable)
    {
        Require(receivers.Length == amounts.Length, "LENGTH_MISMATCH");

        var streamIds = new ulong[receivers.Length];
        for (int i = 0; i < receivers.Length; i++)
        {
            streamIds[i] = CreateStream(
                receivers[i], token, amounts[i],
                startBlock, endBlock, cliffBlock,
                cancelable, Array.Empty<byte>());
        }
        return streamIds;
    }

    // --- Claiming ---

    /// <summary>
    /// Claim accrued (unlocked) tokens from a stream. Only the receiver
    /// (or authorized delegate) can claim.
    /// </summary>
    public UInt256 Claim(ulong streamId, UInt256 amount)
    {
        var stream = _streams.Get(streamId);
        Require(stream.Receiver == Context.Sender, "NOT_RECEIVER");
        Require(stream.Status == StreamStatus.Active, "NOT_ACTIVE");

        var accrued = CalculateAccrued(stream);
        var claimable = accrued - stream.ClaimedAmount;
        Require(claimable > UInt256.Zero, "NOTHING_TO_CLAIM");

        var claimAmount = UInt256.Min(amount, claimable);

        // Deduct protocol fee
        var fee = claimAmount * _protocolFeeBps.Get() / 10000;
        var netAmount = claimAmount - fee;

        stream.ClaimedAmount += claimAmount;

        // Check if stream is fully claimed
        if (stream.ClaimedAmount >= stream.TotalAmount)
            stream.Status = StreamStatus.Completed;

        _streams.Set(streamId, stream);

        TransferTokenOut(stream.Token, Context.Sender, netAmount);
        if (fee > UInt256.Zero)
            TransferTokenOut(stream.Token, _feeRecipient.Get(), fee);

        EmitEvent("Claimed", streamId, Context.Sender, claimAmount);
        return netAmount;
    }

    /// <summary>
    /// Claim all available tokens from a stream.
    /// </summary>
    public UInt256 ClaimAll(ulong streamId)
    {
        var stream = _streams.Get(streamId);
        var accrued = CalculateAccrued(stream);
        var claimable = accrued - stream.ClaimedAmount;
        return Claim(streamId, claimable);
    }

    // --- Cancellation ---

    /// <summary>
    /// Cancel a stream. Accrued tokens go to receiver; unstreamed
    /// tokens are refunded to sender. Only works on cancelable streams.
    /// </summary>
    public (UInt256 receiverAmount, UInt256 senderRefund) Cancel(ulong streamId)
    {
        var stream = _streams.Get(streamId);
        Require(stream.Sender == Context.Sender, "NOT_SENDER");
        Require(stream.Cancelable, "NOT_CANCELABLE");
        Require(stream.Status == StreamStatus.Active, "NOT_ACTIVE");

        var accrued = CalculateAccrued(stream);
        var receiverAmount = accrued - stream.ClaimedAmount;
        var senderRefund = stream.TotalAmount - accrued;

        stream.Status = StreamStatus.Canceled;
        stream.ClaimedAmount = accrued;
        _streams.Set(streamId, stream);

        // Transfer accrued to receiver
        if (receiverAmount > UInt256.Zero)
            TransferTokenOut(stream.Token, stream.Receiver, receiverAmount);

        // Refund unstreamed to sender
        if (senderRefund > UInt256.Zero)
            TransferTokenOut(stream.Token, stream.Sender, senderRefund);

        EmitEvent("StreamCanceled", streamId, receiverAmount, senderRefund);
        return (receiverAmount, senderRefund);
    }

    // --- Stream Transfer ---

    /// <summary>
    /// Transfer a stream's receiver position to a new address.
    /// Only the current receiver can transfer.
    /// </summary>
    public void TransferStream(ulong streamId, Address newReceiver)
    {
        var stream = _streams.Get(streamId);
        Require(stream.Receiver == Context.Sender, "NOT_RECEIVER");
        Require(stream.Status == StreamStatus.Active, "NOT_ACTIVE");
        Require(newReceiver != Address.Zero, "ZERO_ADDRESS");

        stream.Receiver = newReceiver;
        _streams.Set(streamId, stream);

        // Update indices
        EmitEvent("StreamTransferred", streamId, Context.Sender, newReceiver);
    }

    // --- Top-Up ---

    /// <summary>
    /// Add more tokens to an existing stream, extending its effective
    /// rate or end block.
    /// </summary>
    public void TopUpStream(ulong streamId, UInt256 additionalAmount, ulong newEndBlock)
    {
        var stream = _streams.Get(streamId);
        Require(stream.Sender == Context.Sender, "NOT_SENDER");
        Require(stream.Status == StreamStatus.Active, "NOT_ACTIVE");
        Require(newEndBlock >= stream.EndBlock, "CANNOT_SHORTEN");

        TransferTokenIn(stream.Token, Context.Sender, additionalAmount);

        stream.TotalAmount += additionalAmount;
        stream.EndBlock = newEndBlock;
        _streams.Set(streamId, stream);

        EmitEvent("StreamToppedUp", streamId, additionalAmount, newEndBlock);
    }

    // --- Accrual Calculation ---

    /// <summary>
    /// Calculate the total accrued (unlocked) amount for a stream
    /// based on blocks elapsed. Respects cliff period.
    /// </summary>
    public UInt256 CalculateAccrued(Stream stream)
    {
        if (Context.BlockNumber < stream.StartBlock)
            return UInt256.Zero;

        // Cliff check: nothing accrued until cliff
        if (stream.CliffBlock > 0 && Context.BlockNumber < stream.CliffBlock)
            return UInt256.Zero;

        // Fully vested after end block
        if (Context.BlockNumber >= stream.EndBlock)
            return stream.TotalAmount;

        // Linear accrual
        var elapsed = Context.BlockNumber - stream.StartBlock;
        var duration = stream.EndBlock - stream.StartBlock;
        return (stream.TotalAmount * elapsed) / duration;
    }

    /// <summary>
    /// Get the currently claimable (accrued minus already claimed) amount.
    /// </summary>
    public UInt256 GetClaimable(ulong streamId)
    {
        var stream = _streams.Get(streamId);
        if (stream.Status != StreamStatus.Active)
            return UInt256.Zero;
        var accrued = CalculateAccrued(stream);
        return accrued - stream.ClaimedAmount;
    }

    // --- Queries ---

    public Stream GetStream(ulong streamId) => _streams.Get(streamId);

    public UInt256 GetStreamRate(ulong streamId)
    {
        var stream = _streams.Get(streamId);
        var duration = stream.EndBlock - stream.StartBlock;
        if (duration == 0) return stream.TotalAmount;
        return stream.TotalAmount / duration;
    }

    public ulong GetSenderStreamCount(Address sender)
        => _senderStreamCount.Get(sender);

    public ulong GetReceiverStreamCount(Address receiver)
        => _receiverStreamCount.Get(receiver);

    public ulong GetSenderStreamId(Address sender, ulong index)
    {
        var key = ComputeIndexKey(sender, index);
        return _senderStreamIndex.Get(key);
    }

    public ulong GetReceiverStreamId(Address receiver, ulong index)
    {
        var key = ComputeIndexKey(receiver, index);
        return _receiverStreamIndex.Get(key);
    }

    // --- Internal Helpers ---

    private void IndexStream(Address sender, Address receiver, ulong streamId)
    {
        var senderCount = _senderStreamCount.Get(sender);
        _senderStreamIndex.Set(ComputeIndexKey(sender, senderCount), streamId);
        _senderStreamCount.Set(sender, senderCount + 1);

        var receiverCount = _receiverStreamCount.Get(receiver);
        _receiverStreamIndex.Set(ComputeIndexKey(receiver, receiverCount), streamId);
        _receiverStreamCount.Set(receiver, receiverCount + 1);
    }

    private Hash256 ComputeIndexKey(Address addr, ulong index) { /* ... */ }
    private void TransferTokenIn(Address token, Address from, UInt256 amount) { /* ... */ }
    private void TransferTokenOut(Address token, Address to, UInt256 amount) { /* ... */ }
    private void RequireGovernance() { /* ... */ }
}
```

## Complexity

**Low** -- Streaming payment logic is mathematically simple: linear interpolation between start and end blocks. The main engineering considerations are gas-efficient index management for enumeration, correct handling of cliff periods, batch creation for payroll use cases, and stream transfer mechanics. No oracle dependencies, no complex financial math.

## Priority

**P2** -- Streaming payments are valuable for ecosystem adoption (payroll, vesting, subscriptions) but are not a prerequisite for other DeFi protocols. They can be deployed at any time after core infrastructure is live. High user-facing value but low systemic dependency.
