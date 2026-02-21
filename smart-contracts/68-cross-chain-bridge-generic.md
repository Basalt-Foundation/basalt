# Generic Cross-Chain Message Bridge

## Category

Infrastructure -- Cross-Chain Interoperability

## Summary

A generalized cross-chain message passing protocol that extends Basalt's existing BridgeETH pattern to support arbitrary calldata relay between Basalt and external blockchains. Messages are verified by an M-of-N Ed25519 multisig relayer set and processed in nonce order, enabling cross-chain governance execution, token transfers, NFT bridging, and arbitrary contract invocations across chain boundaries.

## Why It's Useful

- **Chain Interoperability**: The blockchain ecosystem is fragmented across dozens of L1s and L2s. A generic message bridge allows Basalt to communicate with all of them through a single unified protocol rather than building bespoke bridges for each chain.
- **Cross-Chain Composability**: DeFi protocols on Basalt can interact with liquidity and protocols on Ethereum, Solana, and other chains, vastly expanding the available capital and user base.
- **Unified Governance**: DAOs with assets or operations on multiple chains can manage everything through a single governance system on Basalt, relaying execution messages to other chains.
- **Asset Portability**: Users can move any supported asset (tokens, NFTs, credentials) between chains without relying on centralized exchanges.
- **Developer Simplicity**: A single contract interface for all cross-chain operations reduces integration complexity for protocol developers.
- **Censorship Resistance**: M-of-N relayer verification with a decentralized relayer set prevents any single entity from censoring cross-chain messages.

## Key Features

- **Arbitrary Calldata Relay**: Send any encoded calldata to any contract on a supported destination chain. Not limited to token transfers.
- **M-of-N Ed25519 Relayer Verification**: Messages are attested by a configurable threshold of relayers (e.g., 5-of-8). Relayer set managed by governance.
- **Nonce-Ordered Message Queue**: Messages are processed strictly in order per source-destination-sender tuple, preventing replay and ensuring causal consistency.
- **Multi-Chain Support**: Chain registry maps chain IDs to configuration (confirmation requirements, gas limits, message size limits). New chains added by governance.
- **Retry and Timeout Mechanism**: Failed message deliveries can be retried. Messages that are not delivered within a timeout period can be refunded or cancelled.
- **Fee Estimation**: On-chain fee estimation for cross-chain messages based on destination chain gas costs, relayer fees, and message size.
- **Message Batching**: Multiple messages to the same destination chain can be batched into a single relay transaction, amortizing relayer costs.
- **Emergency Pause**: Per-chain pause capability for rapid response to bridge exploits or chain reorganizations.
- **Inbound Message Verification**: Messages arriving on Basalt from other chains are verified against relayer attestations before execution.
- **Outbound Message Queue**: Messages leaving Basalt are queued and emitted as events for relayers to observe and relay.
- **Replay Protection**: Each message has a unique nonce and chain-specific identifier. Processed messages are marked to prevent double-execution.
- **Gas Refund for Relayers**: Relayers are compensated from message fees for gas spent on relay transactions.
- **Rate Limiting**: Per-chain rate limits on message volume to prevent abuse and provide time for monitoring.

## Basalt-Specific Advantages

- **Extends Proven BridgeETH Pattern**: Built on the same M-of-N Ed25519 multisig verification, deposit lifecycle (pending, confirmed, finalized), and replay protection patterns already battle-tested in BridgeETH (0x...1008). The generic bridge is a natural evolution, not a greenfield design.
- **Ed25519 Multisig Efficiency**: Basalt's native Ed25519 signatures are faster to verify than ECDSA, and the multisig verification pattern (already implemented in BridgeETH) provides strong security with lower gas costs.
- **BLS Aggregate Relayer Signatures**: Future optimization can aggregate relayer attestations into a single BLS signature via Basalt's BLS12-381 support, reducing verification from O(N) to O(1) for N relayers.
- **AOT-Compiled Message Processing**: Complex calldata decoding and routing logic executes in AOT-compiled native code, enabling higher message throughput than interpreted EVM bridges.
- **ZK Compliance for Cross-Chain Transfers**: Token transfers crossing chains can require ZK compliance proofs, ensuring that bridged assets on Basalt remain within the compliance framework. This is unique to Basalt -- no other bridge enforces ZK compliance natively.
- **Confidential Cross-Chain Transfers**: Pedersen commitment support enables hiding transfer amounts in cross-chain messages, preventing front-running and privacy leakage during bridging.

## Token Standards Used

- **BST-20**: Bridged fungible tokens are represented as BST-20 on Basalt. Message fees are paid in BST-20 (BST or WBSLT).
- **BST-721**: Cross-chain NFT bridging creates BST-721 representations on Basalt with preserved metadata.
- **BST-3525 (SFT)**: Complex cross-chain positions (LP tokens, vesting schedules) can be bridged as BST-3525 with slot metadata.
- **BST-VC (Verifiable Credentials)**: Cross-chain identity attestations can be bridged as BST-VC credentials on Basalt.

## Integration Points

- **BridgeETH (0x...1008)**: The generic bridge subsumes and extends BridgeETH. Existing BridgeETH functionality for BST-ETH transfers continues through the generic bridge's Ethereum channel.
- **Governance (0x0102)**: Governs relayer set changes, chain registry additions, fee parameters, pause/unpause, and rate limits. Cross-chain governance execution uses the bridge to relay governance decisions to other chains.
- **Escrow (0x0103)**: Inbound bridged assets are held in escrow until finalization confirmations are met.
- **SchemaRegistry (0x...1006)**: Cross-chain credential bridging verifies schema compatibility.
- **IssuerRegistry (0x...1007)**: Cross-chain credential issuers must be registered or mapped to Basalt issuers.
- **BNS (0x0101)**: Bridge endpoints registered under BNS names (e.g., `bridge.ethereum.basalt`, `bridge.solana.basalt`).
- **StakingPool (0x0105)**: Relayer staking requirements and slashing for misbehavior (attesting invalid messages).

## Technical Sketch

```csharp
// ============================================================
// GenericBridge -- Cross-chain message passing protocol
// ============================================================

[BasaltContract(TypeId = 0x0302)]
public partial class GenericBridge : SdkContract
{
    // --- Storage ---

    // Outbound message counter (global nonce)
    private StorageValue<ulong> _outboundNonce;

    // Inbound message tracking: messageHash => processed
    private StorageMap<Hash256, bool> _processedMessages;

    // Relayer set: index => relayer public key (Ed25519, 32 bytes)
    private StorageMap<uint, byte[]> _relayers;
    private StorageValue<uint> _relayerCount;
    private StorageValue<uint> _requiredSignatures; // M in M-of-N

    // Chain registry: chainId => ChainConfig
    private StorageMap<uint, ChainConfig> _chains;
    private StorageMap<uint, bool> _chainPaused;

    // Outbound queue: nonce => OutboundMessage
    private StorageMap<ulong, OutboundMessage> _outboundQueue;

    // Per-chain rate limiting
    private StorageMap<uint, ulong> _messageCountThisEpoch;
    private StorageMap<uint, ulong> _rateLimitPerEpoch;
    private StorageMap<uint, ulong> _currentEpochStart;

    // Fee parameters
    private StorageValue<UInt256> _baseFee;
    private StorageMap<uint, UInt256> _perChainFeeMultiplier; // basis points

    // Relayer compensation pool
    private StorageValue<UInt256> _relayerPool;

    // Admin / governance
    private StorageValue<Address> _admin;
    private StorageValue<bool> _globalPaused;

    // --- Data Structures ---

    public struct ChainConfig
    {
        public uint ChainId;
        public string ChainName;
        public uint RequiredConfirmations;
        public uint MaxMessageSize;        // bytes
        public ulong MaxGasLimit;
        public bool Active;
    }

    public struct OutboundMessage
    {
        public ulong Nonce;
        public uint DestinationChainId;
        public Address Sender;
        public byte[] DestinationAddress;  // 20 or 32 bytes depending on chain
        public byte[] Calldata;
        public UInt256 Value;              // Native token amount to bridge
        public ulong GasLimit;
        public ulong Timestamp;
        public byte Status;               // 0=queued, 1=relayed, 2=confirmed, 3=failed
    }

    public struct InboundMessage
    {
        public uint SourceChainId;
        public ulong SourceNonce;
        public byte[] SourceSender;
        public Address DestinationAddress;
        public byte[] Calldata;
        public UInt256 Value;
        public ulong GasLimit;
    }

    public struct RelayerAttestation
    {
        public uint RelayerIndex;
        public byte[] Signature;           // Ed25519 signature over message hash
    }

    // --- Outbound (Basalt => External Chain) ---

    /// <summary>
    /// Send a cross-chain message to an external chain.
    /// The message is queued for relayer pickup.
    /// </summary>
    public ulong SendMessage(
        uint destinationChainId,
        byte[] destinationAddress,
        byte[] calldata,
        ulong gasLimit)
    {
        Require(!_globalPaused.Get(), "BRIDGE_PAUSED");
        Require(!_chainPaused.Get(destinationChainId), "CHAIN_PAUSED");

        var chain = _chains.Get(destinationChainId);
        Require(chain.Active, "CHAIN_NOT_SUPPORTED");
        Require(calldata.Length <= chain.MaxMessageSize, "MESSAGE_TOO_LARGE");
        Require(gasLimit <= chain.MaxGasLimit, "GAS_LIMIT_EXCEEDED");

        // Check rate limit
        EnforceRateLimit(destinationChainId);

        // Calculate and collect fee
        UInt256 fee = CalculateMessageFee(destinationChainId, calldata.Length, gasLimit);
        Require(Context.TxValue >= fee, "INSUFFICIENT_FEE");
        _relayerPool.Set(_relayerPool.Get() + fee);

        // Queue outbound message
        ulong nonce = _outboundNonce.Get();
        _outboundNonce.Set(nonce + 1);

        var message = new OutboundMessage
        {
            Nonce = nonce,
            DestinationChainId = destinationChainId,
            Sender = Context.Sender,
            DestinationAddress = destinationAddress,
            Calldata = calldata,
            Value = Context.TxValue - fee,
            GasLimit = gasLimit,
            Timestamp = Context.BlockNumber,
            Status = 0 // queued
        };

        _outboundQueue.Set(nonce, message);

        EmitEvent("MessageSent", nonce, destinationChainId,
                  Context.Sender, destinationAddress, calldata);
        return nonce;
    }

    /// <summary>
    /// Send a cross-chain token transfer (convenience wrapper).
    /// Locks native BST and sends a mint message to the destination chain.
    /// </summary>
    public ulong SendTokenTransfer(
        uint destinationChainId,
        byte[] destinationAddress,
        UInt256 amount,
        byte[] complianceProof)
    {
        Require(amount > UInt256.Zero, "ZERO_AMOUNT");

        // Optional ZK compliance check for cross-chain transfers
        if (complianceProof.Length > 0)
        {
            bool compliant = VerifyComplianceProof(complianceProof);
            Require(compliant, "COMPLIANCE_FAILED");
        }

        // Encode mint calldata for destination chain
        byte[] calldata = EncodeTokenMint(destinationAddress, amount);

        return SendMessage(destinationChainId, GetBridgeAddress(destinationChainId),
                          calldata, 100000);
    }

    // --- Inbound (External Chain => Basalt) ---

    /// <summary>
    /// Process an inbound cross-chain message with M-of-N relayer attestations.
    /// </summary>
    public void ProcessInboundMessage(
        InboundMessage message,
        RelayerAttestation[] attestations)
    {
        Require(!_globalPaused.Get(), "BRIDGE_PAUSED");
        Require(!_chainPaused.Get(message.SourceChainId), "CHAIN_PAUSED");

        // Compute message hash
        Hash256 messageHash = ComputeMessageHash(message);

        // Prevent replay
        Require(!_processedMessages.Get(messageHash), "ALREADY_PROCESSED");

        // Verify M-of-N relayer signatures
        uint validSignatures = 0;
        uint requiredSigs = _requiredSignatures.Get();
        bool[] usedRelayers = new bool[_relayerCount.Get()];

        for (int i = 0; i < attestations.Length; i++)
        {
            var att = attestations[i];
            Require(att.RelayerIndex < _relayerCount.Get(), "INVALID_RELAYER");
            Require(!usedRelayers[att.RelayerIndex], "DUPLICATE_RELAYER");

            byte[] relayerKey = _relayers.Get(att.RelayerIndex);
            Require(relayerKey.Length == 32, "RELAYER_NOT_SET");

            bool valid = VerifyEd25519Signature(relayerKey, messageHash.ToArray(),
                                                 att.Signature);
            if (valid)
            {
                validSignatures++;
                usedRelayers[att.RelayerIndex] = true;
            }
        }

        Require(validSignatures >= requiredSigs, "INSUFFICIENT_SIGNATURES");

        // Mark as processed
        _processedMessages.Set(messageHash, true);

        // Execute the message on Basalt
        if (message.Calldata.Length > 0)
        {
            Context.Call(message.DestinationAddress, message.Calldata,
                        message.Value, message.GasLimit);
        }
        else if (message.Value > UInt256.Zero)
        {
            // Pure value transfer
            Context.TransferNative(message.DestinationAddress, message.Value);
        }

        EmitEvent("MessageProcessed", messageHash, message.SourceChainId,
                  message.SourceNonce, message.DestinationAddress);
    }

    // --- Message Batching ---

    /// <summary>
    /// Process multiple inbound messages in a single transaction.
    /// All messages must be from the same source chain.
    /// </summary>
    public void ProcessBatch(
        InboundMessage[] messages,
        RelayerAttestation[] batchAttestations,
        Hash256 batchHash)
    {
        // Verify attestations over the batch hash
        VerifyBatchAttestations(batchHash, batchAttestations);

        // Verify batch hash matches messages
        Hash256 computedBatchHash = ComputeBatchHash(messages);
        Require(computedBatchHash == batchHash, "BATCH_HASH_MISMATCH");

        // Process each message
        for (int i = 0; i < messages.Length; i++)
        {
            Hash256 messageHash = ComputeMessageHash(messages[i]);
            if (!_processedMessages.Get(messageHash))
            {
                _processedMessages.Set(messageHash, true);
                ExecuteMessage(messages[i]);
            }
        }

        EmitEvent("BatchProcessed", batchHash, (ulong)messages.Length);
    }

    // --- Relayer Management ---

    /// <summary>
    /// Add a relayer to the set. Governance-only.
    /// </summary>
    public void AddRelayer(byte[] relayerPublicKey)
    {
        RequireGovernance();
        Require(relayerPublicKey.Length == 32, "INVALID_KEY");

        uint index = _relayerCount.Get();
        _relayers.Set(index, relayerPublicKey);
        _relayerCount.Set(index + 1);

        EmitEvent("RelayerAdded", index, relayerPublicKey);
    }

    /// <summary>
    /// Remove a relayer. Governance-only.
    /// </summary>
    public void RemoveRelayer(uint relayerIndex)
    {
        RequireGovernance();
        Require(relayerIndex < _relayerCount.Get(), "INVALID_INDEX");
        _relayers.Set(relayerIndex, new byte[0]);
        EmitEvent("RelayerRemoved", relayerIndex);
    }

    /// <summary>
    /// Update the required signature threshold. Governance-only.
    /// </summary>
    public void SetRequiredSignatures(uint required)
    {
        RequireGovernance();
        Require(required > 0 && required <= _relayerCount.Get(),
                "INVALID_THRESHOLD");
        _requiredSignatures.Set(required);
        EmitEvent("ThresholdUpdated", required);
    }

    // --- Chain Management ---

    /// <summary>
    /// Register a new supported chain. Governance-only.
    /// </summary>
    public void RegisterChain(ChainConfig config)
    {
        RequireGovernance();
        _chains.Set(config.ChainId, config);
        EmitEvent("ChainRegistered", config.ChainId, config.ChainName);
    }

    /// <summary>
    /// Pause/unpause a specific chain. Admin-only for emergencies.
    /// </summary>
    public void SetChainPaused(uint chainId, bool paused)
    {
        RequireAdminOrGovernance();
        _chainPaused.Set(chainId, paused);
        EmitEvent("ChainPauseUpdated", chainId, paused);
    }

    /// <summary>
    /// Global pause. Admin-only for emergencies.
    /// </summary>
    public void SetGlobalPause(bool paused)
    {
        RequireAdminOrGovernance();
        _globalPaused.Set(paused);
        EmitEvent("GlobalPauseUpdated", paused);
    }

    // --- Relayer Compensation ---

    /// <summary>
    /// Claim relayer compensation for gas spent on relay transactions.
    /// </summary>
    public void ClaimRelayerCompensation(uint relayerIndex, UInt256 amount,
                                          byte[] proof)
    {
        byte[] relayerKey = _relayers.Get(relayerIndex);
        Require(relayerKey.Length == 32, "INVALID_RELAYER");
        Require(DeriveAddress(relayerKey) == Context.Sender, "NOT_RELAYER");

        UInt256 poolBalance = _relayerPool.Get();
        Require(amount <= poolBalance, "INSUFFICIENT_POOL");

        _relayerPool.Set(poolBalance - amount);
        Context.TransferNative(Context.Sender, amount);

        EmitEvent("RelayerCompensated", relayerIndex, amount);
    }

    // --- Fee Calculation ---

    /// <summary>
    /// Estimate the fee for sending a cross-chain message.
    /// </summary>
    public UInt256 EstimateFee(uint destinationChainId, uint messageSize,
                               ulong gasLimit)
    {
        return CalculateMessageFee(destinationChainId, messageSize, gasLimit);
    }

    // --- Query Methods ---

    public OutboundMessage GetOutboundMessage(ulong nonce) => _outboundQueue.Get(nonce);
    public bool IsMessageProcessed(Hash256 messageHash) => _processedMessages.Get(messageHash);
    public ChainConfig GetChainConfig(uint chainId) => _chains.Get(chainId);
    public uint GetRelayerCount() => _relayerCount.Get();
    public uint GetRequiredSignatures() => _requiredSignatures.Get();
    public UInt256 GetRelayerPoolBalance() => _relayerPool.Get();
    public ulong GetOutboundNonce() => _outboundNonce.Get();
    public bool IsChainPaused(uint chainId) => _chainPaused.Get(chainId);

    // --- Internal Helpers ---

    private UInt256 CalculateMessageFee(uint chainId, int messageSize, ulong gasLimit)
    {
        UInt256 baseFee = _baseFee.Get();
        UInt256 multiplier = _perChainFeeMultiplier.Get(chainId);
        if (multiplier.IsZero) multiplier = 10000; // default 1x
        UInt256 sizeFee = baseFee * (ulong)messageSize / 1000;
        UInt256 gasFee = baseFee * gasLimit / 1000000;
        return ((baseFee + sizeFee + gasFee) * multiplier) / 10000;
    }

    private void EnforceRateLimit(uint chainId)
    {
        ulong limit = _rateLimitPerEpoch.Get(chainId);
        if (limit == 0) return; // no rate limit

        ulong epochStart = _currentEpochStart.Get(chainId);
        ulong epochLength = 100; // blocks per epoch
        if (Context.BlockNumber >= epochStart + epochLength)
        {
            _currentEpochStart.Set(chainId, Context.BlockNumber);
            _messageCountThisEpoch.Set(chainId, 0);
        }

        ulong count = _messageCountThisEpoch.Get(chainId);
        Require(count < limit, "RATE_LIMIT_EXCEEDED");
        _messageCountThisEpoch.Set(chainId, count + 1);
    }

    private Hash256 ComputeMessageHash(InboundMessage msg) { /* BLAKE3 hash */ }
    private Hash256 ComputeBatchHash(InboundMessage[] msgs) { /* ... */ }
    private void VerifyBatchAttestations(Hash256 hash, RelayerAttestation[] atts) { /* ... */ }
    private void ExecuteMessage(InboundMessage msg) { /* ... */ }
    private bool VerifyEd25519Signature(byte[] pubKey, byte[] msg, byte[] sig) { /* ... */ }
    private bool VerifyComplianceProof(byte[] proof) { /* ... */ }
    private byte[] EncodeTokenMint(byte[] dest, UInt256 amount) { /* ... */ }
    private byte[] GetBridgeAddress(uint chainId) { /* ... */ }
    private Address DeriveAddress(byte[] publicKey) { /* ... */ }
    private void RequireGovernance() { /* ... */ }
    private void RequireAdminOrGovernance() { /* ... */ }
}
```

## Complexity

**High** -- Cross-chain message bridges are among the most security-critical components in blockchain infrastructure, as evidenced by the billions of dollars lost in bridge exploits across the industry. Correct implementation requires careful handling of: M-of-N signature verification with replay protection, nonce ordering across asynchronous systems, message batching atomicity, rate limiting, fee economics for relayer incentivization, and emergency pause mechanisms. The multi-chain abstraction adds complexity around chain-specific encoding, confirmation requirements, and gas estimation. Security auditing of bridge contracts requires specialized expertise.

## Priority

**P1** -- Cross-chain interoperability is essential for Basalt's adoption. Without the ability to move assets and data between Basalt and established chains (Ethereum, Solana, etc.), the ecosystem remains isolated. The existing BridgeETH contract demonstrates the pattern; generalizing it to arbitrary message passing is the natural next step. This should be prioritized after core DeFi primitives but before advanced application-layer contracts.
