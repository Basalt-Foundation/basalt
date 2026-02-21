# Cross-Chain Atomic Swap

## Category

Cross-Chain / Interoperability / Trustless Exchange

## Summary

A Hash Time-Locked Contract (HTLC) that enables trustless cross-chain swaps between Basalt and other blockchains. Users lock tokens with a BLAKE3 hashlock and a block-number-based timelock. The counterparty on the other chain locks their tokens with the same hashlock. When one party reveals the preimage to claim their tokens, the other party can use the same preimage to claim theirs. The contract integrates with BridgeETH for coordinated EVM-side swaps, eliminating the need for any trusted intermediary.

## Why It's Useful

- **Trustless Cross-Chain Trading**: Atomic swaps are the gold standard for cross-chain token exchange without requiring a centralized exchange, bridge operator, or any form of trust. Either both sides of the swap complete, or neither does.
- **No Intermediary Risk**: Unlike bridge-based transfers where tokens are locked in a bridge contract controlled by a multisig, atomic swaps have no intermediary holding funds. Each party's tokens are locked in their own chain's HTLC, and the cryptographic hashlock ensures atomicity.
- **Censorship Resistance**: No centralized party can block or censor an atomic swap. As long as both blockchains are operational, the swap can proceed.
- **Price Discovery**: Atomic swaps enable direct peer-to-peer trading of assets across chains at mutually agreed prices, contributing to cross-chain price discovery without relying on centralized order books.
- **Bridge Complement**: While the BridgeETH contract handles wrapped token transfers, atomic swaps enable native token exchange. A user can swap native BST for native ETH without either token ever being wrapped or locked in a bridge.
- **Escrow Alternative**: For large OTC trades, atomic swaps provide a trustless escrow mechanism that works across chains. Two parties agree on terms off-chain, execute the swap on-chain, and neither party can default.
- **DEX Aggregation**: Atomic swap contracts on multiple chains can be composed by DEX aggregators to find the best cross-chain trading routes, improving liquidity access for users.

## Key Features

- **BLAKE3 Hashlock**: The hashlock uses BLAKE3(preimage) rather than SHA-256 or Keccak-256. BLAKE3's speed advantage is significant for on-chain verification, and it is Basalt's native hash function.
- **Block Number Timelock**: The timelock is expressed in block numbers rather than timestamps, providing deterministic expiry that is not subject to timestamp manipulation. The initiator's timelock must be longer than the responder's to ensure the initiator cannot claim both sides.
- **Multi-Token Support**: The HTLC supports locking BST (native token) or any BST-20 token. This enables atomic swaps of any Basalt-native asset.
- **Refund After Expiry**: If the counterparty does not claim within the timelock period, the initiator can refund their locked tokens. This ensures no funds are permanently locked.
- **Preimage Revelation**: When a party claims locked tokens by providing the preimage, the preimage is emitted as part of the transaction state change. The counterparty (or any observer) can read this preimage from the claiming chain and use it to claim on the other chain.
- **Swap Coordination Protocol**: The contract includes a state machine (Initiated -> Claimed/Refunded) that tracks each swap's lifecycle. Observers can monitor pending swaps and match them with their cross-chain counterparts.
- **BridgeETH Integration**: For Basalt-to-Ethereum swaps, the contract can coordinate with BridgeETH to set up the EVM-side HTLC. The bridge relayers can relay hashlock parameters, simplifying the cross-chain setup.
- **Batch Swaps**: Multiple HTLCs can be created in a single transaction, enabling atomic multi-leg trades (e.g., BST -> ETH -> USDC across three chains).
- **Partial Claims**: Optional support for partial claims where the claimant can claim a portion of the locked tokens by revealing a partial preimage (using a Merkle tree of preimages). This enables streaming cross-chain payments.
- **Swap Discovery**: A registry of open swap offers that other users can browse and accept. This creates a decentralized order book for cross-chain trades.

## Basalt-Specific Advantages

- **BLAKE3 Native Hashlock**: Basalt's native BLAKE3 hashing function is the fastest cryptographic hash available. On EVM chains, HTLCs typically use SHA-256 or Keccak-256, both of which are slower. BLAKE3 hashlock creation and verification on Basalt is significantly more gas-efficient, making atomic swaps cheaper.
- **Ed25519 Cross-Chain Compatibility**: Basalt's Ed25519 signatures are compatible with many non-EVM chains (Solana, Near, Cosmos, Polkadot) that also use Ed25519. This means the same signing key can be used on both sides of a Basalt-to-Solana atomic swap, simplifying the user experience.
- **BridgeETH Coordination**: Basalt's native bridge contract provides a built-in coordination layer for EVM-side HTLCs. The bridge relayers can relay hashlock parameters and monitor swap state across chains, reducing the operational burden on users.
- **AOT-Compiled Swap Matching**: The swap discovery registry, which requires iterating through open offers and matching against user criteria, runs as native compiled code. This makes on-chain order matching feasible for a decentralized cross-chain order book.
- **ZK Compliance for Regulated Swaps**: When swapping tokens that are subject to compliance requirements (e.g., security tokens), both parties can provide ZK compliance proofs. The HTLC verifies compliance without revealing party identities, enabling regulated cross-chain trading.
- **Pedersen Commitment Privacy**: Swap amounts can be hidden using Pedersen commitments. The HTLC verifies that both sides commit to consistent amounts (using range proofs) without revealing the exact trade size to observers. This prevents front-running and trade-size analysis.
- **BST-3525 Swap Receipts**: Each HTLC position is represented as a BST-3525 token, enabling position transfer (the right to claim or refund can be transferred to another party) and standardized position management.

## Token Standards Used

- **BST-20**: Any BST-20 token can be locked in the HTLC for cross-chain swap. The native BST token is also supported via direct value transfer.
- **BST-3525 (SFT)**: HTLC positions are represented as BST-3525 semi-fungible tokens. The slot represents the swap pair, and the value represents the locked amount. Positions can be transferred.

## Integration Points

- **BridgeETH (0x...1008)**: The primary integration for Basalt-to-Ethereum atomic swaps. BridgeETH relayers coordinate the EVM-side HTLC setup and monitor cross-chain claim events to relay preimages.
- **BNS (0x...1001)**: Swap offers can reference counterparty by BNS name. The HTLC contract registers a BNS name (e.g., "swap.bst") for discoverability.
- **Escrow (0x...1003)**: For high-value swaps, an additional Escrow layer can add a dispute resolution mechanism. If one party claims but the preimage is not properly relayed to the other chain, the Escrow contract can hold funds during a dispute period.
- **Governance (0x...1002)**: HTLC parameters (minimum timelock duration, maximum swap duration, supported cross-chain pairs) are governed via Governance proposals.
- **SchemaRegistry (0x...1006)**: Credential schemas for compliance-gated swaps are stored in the SchemaRegistry.
- **IssuerRegistry (0x...1007)**: Validates credential issuers for ZK compliance proofs required for regulated cross-chain trades.

## Technical Sketch

```csharp
// Contract type ID: 0x010E
[BasaltContract(0x010E)]
public partial class AtomicSwap : SdkContract, IDispatchable
{
    // --- Enums ---

    public enum SwapState : byte
    {
        Empty = 0,
        Initiated = 1,
        Claimed = 2,
        Refunded = 3
    }

    // --- Storage ---

    private StorageValue<Address> _admin;
    private StorageValue<ulong> _nextSwapId;
    private StorageValue<ulong> _minTimelockBlocks;       // minimum timelock duration
    private StorageValue<ulong> _maxTimelockBlocks;       // maximum timelock duration
    private StorageValue<bool> _paused;

    // Swap fields (keyed by swapId)
    private StorageMap<ulong, Address> _swapInitiator;
    private StorageMap<ulong, Address> _swapParticipant;   // counterparty on Basalt side
    private StorageMap<ulong, Address> _swapToken;         // Address.Zero for native BST
    private StorageMap<ulong, UInt256> _swapAmount;
    private StorageMap<ulong, byte> _swapState;

    // Hashlock: BLAKE3 hash of the preimage
    private StorageMap<ulong, ulong> _swapHashlockHi;     // upper 8 bytes
    private StorageMap<ulong, ulong> _swapHashlockLo;     // lower 8 bytes (simplified; full impl uses 32 bytes)
    // In practice, store full 32-byte hashlock via composite storage keys

    private StorageMap<ulong, ulong> _swapTimelockBlock;   // block number after which refund is possible
    private StorageMap<ulong, ulong> _swapCreatedBlock;

    // Preimage storage (revealed upon claim)
    private StorageMap<ulong, ulong> _swapPreimageHi;
    private StorageMap<ulong, ulong> _swapPreimageLo;

    // Swap offer registry
    private StorageValue<ulong> _nextOfferId;
    private StorageMap<ulong, Address> _offerCreator;
    private StorageMap<ulong, Address> _offerSourceToken;
    private StorageMap<ulong, UInt256> _offerSourceAmount;
    private StorageMap<ulong, UInt256> _offerWantedAmount;
    private StorageMap<ulong, bool> _offerActive;
    // Cross-chain info stored as string metadata (chain name, address format)

    // --- Constructor ---

    public void Initialize(
        Address admin,
        ulong minTimelockBlocks,
        ulong maxTimelockBlocks)
    {
        _admin.Set(admin);
        _minTimelockBlocks.Set(minTimelockBlocks);
        _maxTimelockBlocks.Set(maxTimelockBlocks);
        _nextSwapId.Set(1);
        _nextOfferId.Set(1);
    }

    // --- Initiate Swap ---

    public ulong Initiate(
        Address participant,
        ulong hashlockHi,
        ulong hashlockLo,
        ulong timelockBlock)
    {
        Require(!_paused.Get(), "Swaps paused");
        UInt256 amount = Context.TxValue;
        Require(!amount.IsZero, "Must lock non-zero amount");
        Require(participant != Address.Zero, "Invalid participant");

        ulong currentBlock = Context.BlockNumber;
        ulong duration = timelockBlock > currentBlock ? timelockBlock - currentBlock : 0;
        Require(duration >= _minTimelockBlocks.Get(), "Timelock too short");
        Require(duration <= _maxTimelockBlocks.Get(), "Timelock too long");

        ulong swapId = _nextSwapId.Get();
        _nextSwapId.Set(swapId + 1);

        _swapInitiator.Set(swapId, Context.Caller);
        _swapParticipant.Set(swapId, participant);
        _swapToken.Set(swapId, Address.Zero); // native BST
        _swapAmount.Set(swapId, amount);
        _swapState.Set(swapId, (byte)SwapState.Initiated);
        _swapHashlockHi.Set(swapId, hashlockHi);
        _swapHashlockLo.Set(swapId, hashlockLo);
        _swapTimelockBlock.Set(swapId, timelockBlock);
        _swapCreatedBlock.Set(swapId, currentBlock);

        return swapId;
    }

    public ulong InitiateWithToken(
        Address token,
        UInt256 amount,
        Address participant,
        ulong hashlockHi,
        ulong hashlockLo,
        ulong timelockBlock)
    {
        Require(!_paused.Get(), "Swaps paused");
        Require(!amount.IsZero, "Must lock non-zero amount");
        Require(participant != Address.Zero, "Invalid participant");

        ulong currentBlock = Context.BlockNumber;
        ulong duration = timelockBlock > currentBlock ? timelockBlock - currentBlock : 0;
        Require(duration >= _minTimelockBlocks.Get(), "Timelock too short");
        Require(duration <= _maxTimelockBlocks.Get(), "Timelock too long");

        // Transfer tokens from initiator to this contract
        TransferTokensFrom(token, Context.Caller, Context.ContractAddress, amount);

        ulong swapId = _nextSwapId.Get();
        _nextSwapId.Set(swapId + 1);

        _swapInitiator.Set(swapId, Context.Caller);
        _swapParticipant.Set(swapId, participant);
        _swapToken.Set(swapId, token);
        _swapAmount.Set(swapId, amount);
        _swapState.Set(swapId, (byte)SwapState.Initiated);
        _swapHashlockHi.Set(swapId, hashlockHi);
        _swapHashlockLo.Set(swapId, hashlockLo);
        _swapTimelockBlock.Set(swapId, timelockBlock);
        _swapCreatedBlock.Set(swapId, currentBlock);

        return swapId;
    }

    // --- Claim (by participant, reveals preimage) ---

    public bool Claim(ulong swapId, ulong preimageHi, ulong preimageLo)
    {
        Require((SwapState)_swapState.Get(swapId) == SwapState.Initiated, "Swap not claimable");
        Require(Context.Caller == _swapParticipant.Get(swapId), "Not participant");

        // Verify hashlock: BLAKE3(preimage) == hashlock
        // Simplified: in practice, hash the full 32-byte preimage
        bool valid = VerifyHashlock(preimageHi, preimageLo,
            _swapHashlockHi.Get(swapId), _swapHashlockLo.Get(swapId));
        Require(valid, "Invalid preimage");

        // Store preimage (for cross-chain observation)
        _swapPreimageHi.Set(swapId, preimageHi);
        _swapPreimageLo.Set(swapId, preimageLo);
        _swapState.Set(swapId, (byte)SwapState.Claimed);

        // Transfer locked tokens to participant
        Address token = _swapToken.Get(swapId);
        UInt256 amount = _swapAmount.Get(swapId);

        if (token == Address.Zero)
            Context.TransferNative(Context.Caller, amount);
        else
            TransferTokensTo(token, Context.Caller, amount);

        return true;
    }

    // --- Refund (by initiator, after timelock expires) ---

    public bool Refund(ulong swapId)
    {
        Require((SwapState)_swapState.Get(swapId) == SwapState.Initiated, "Swap not refundable");
        Require(Context.Caller == _swapInitiator.Get(swapId), "Not initiator");
        Require(Context.BlockNumber >= _swapTimelockBlock.Get(swapId), "Timelock not expired");

        _swapState.Set(swapId, (byte)SwapState.Refunded);

        // Return locked tokens to initiator
        Address token = _swapToken.Get(swapId);
        UInt256 amount = _swapAmount.Get(swapId);

        if (token == Address.Zero)
            Context.TransferNative(Context.Caller, amount);
        else
            TransferTokensTo(token, Context.Caller, amount);

        return true;
    }

    // --- Swap Offer Registry ---

    public ulong CreateOffer(
        Address sourceToken,
        UInt256 sourceAmount,
        UInt256 wantedAmount)
    {
        Require(!_paused.Get(), "Swaps paused");
        Require(!sourceAmount.IsZero, "Source amount must be > 0");
        Require(!wantedAmount.IsZero, "Wanted amount must be > 0");

        ulong offerId = _nextOfferId.Get();
        _nextOfferId.Set(offerId + 1);

        _offerCreator.Set(offerId, Context.Caller);
        _offerSourceToken.Set(offerId, sourceToken);
        _offerSourceAmount.Set(offerId, sourceAmount);
        _offerWantedAmount.Set(offerId, wantedAmount);
        _offerActive.Set(offerId, true);

        return offerId;
    }

    public void CancelOffer(ulong offerId)
    {
        Require(_offerCreator.Get(offerId) == Context.Caller, "Not offer creator");
        Require(_offerActive.Get(offerId), "Offer not active");
        _offerActive.Set(offerId, false);
    }

    public ulong AcceptOffer(ulong offerId, ulong hashlockHi, ulong hashlockLo, ulong timelockBlock)
    {
        Require(_offerActive.Get(offerId), "Offer not active");
        _offerActive.Set(offerId, false);

        Address creator = _offerCreator.Get(offerId);
        // The acceptor initiates the HTLC with the offer creator as participant
        // The offer creator then sets up their side of the swap

        return Initiate(creator, hashlockHi, hashlockLo, timelockBlock);
    }

    // --- Query ---

    public byte GetSwapState(ulong swapId) => _swapState.Get(swapId);
    public Address GetInitiator(ulong swapId) => _swapInitiator.Get(swapId);
    public Address GetParticipant(ulong swapId) => _swapParticipant.Get(swapId);
    public UInt256 GetSwapAmount(ulong swapId) => _swapAmount.Get(swapId);
    public ulong GetTimelockBlock(ulong swapId) => _swapTimelockBlock.Get(swapId);
    public ulong GetPreimageHi(ulong swapId) => _swapPreimageHi.Get(swapId);
    public ulong GetPreimageLo(ulong swapId) => _swapPreimageLo.Get(swapId);

    public bool IsExpired(ulong swapId)
    {
        return Context.BlockNumber >= _swapTimelockBlock.Get(swapId);
    }

    public ulong BlocksUntilExpiry(ulong swapId)
    {
        ulong timelock = _swapTimelockBlock.Get(swapId);
        ulong currentBlock = Context.BlockNumber;
        if (currentBlock >= timelock) return 0;
        return timelock - currentBlock;
    }

    public bool IsOfferActive(ulong offerId) => _offerActive.Get(offerId);
    public Address GetOfferCreator(ulong offerId) => _offerCreator.Get(offerId);
    public UInt256 GetOfferSourceAmount(ulong offerId) => _offerSourceAmount.Get(offerId);
    public UInt256 GetOfferWantedAmount(ulong offerId) => _offerWantedAmount.Get(offerId);

    // --- Admin ---

    public void SetTimelockBounds(ulong minBlocks, ulong maxBlocks)
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        Require(minBlocks < maxBlocks, "Invalid bounds");
        _minTimelockBlocks.Set(minBlocks);
        _maxTimelockBlocks.Set(maxBlocks);
    }

    public void Pause()
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _paused.Set(true);
    }

    public void Unpause()
    {
        Require(Context.Caller == _admin.Get(), "Not admin");
        _paused.Set(false);
    }

    // --- Internal ---

    private bool VerifyHashlock(
        ulong preimageHi, ulong preimageLo,
        ulong hashlockHi, ulong hashlockLo)
    {
        // Concatenate preimage bytes, compute BLAKE3 hash, compare with hashlock
        // Simplified representation -- actual impl operates on full 32-byte arrays
        // byte[] preimage = Concat(preimageHi.ToBytesBE(), preimageLo.ToBytesBE());
        // byte[] hash = Blake3Hasher.Hash(preimage).ToArray();
        // return hash[0..8] == hashlockHi && hash[8..16] == hashlockLo;
        return false; // placeholder
    }

    private void TransferTokensFrom(Address token, Address from, Address to, UInt256 amount)
    {
        // Cross-contract call to BST-20 token.TransferFrom(from, to, amount)
    }

    private void TransferTokensTo(Address token, Address to, UInt256 amount)
    {
        // Cross-contract call to BST-20 token.Transfer(to, amount)
    }
}
```

## Complexity

**Medium** -- The core HTLC logic (lock, claim with preimage, refund after timelock) is a well-understood and relatively simple pattern. The complexity comes from several extensions: coordinating with BridgeETH for EVM-side HTLC setup, the swap offer registry (which is essentially a simple order book), multi-token support requiring BST-20 approval flows, and the 32-byte hashlock/preimage storage using composite keys. The cross-chain coordination protocol (ensuring timelock durations are safe across chains with different block times) adds protocol-level complexity that is not purely a smart contract concern. The BLAKE3 hashlock verification is straightforward but must be implemented correctly to match the hash computation on the counterparty chain.

## Priority

**P1** -- Cross-chain interoperability is critical for any blockchain ecosystem. Atomic swaps are the most trustless form of cross-chain exchange and complement the BridgeETH contract's wrapped token model. Having a native atomic swap contract positions Basalt as interoperability-friendly and enables direct trading with major ecosystems (Ethereum, Solana, Cosmos). This should be built alongside or shortly after BridgeETH, as the two contracts share coordination infrastructure and serve complementary cross-chain use cases.
