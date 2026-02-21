# Multi-Signature Wallet

## Category

Infrastructure / Security

## Summary

A generalized M-of-N multi-signature wallet contract that requires a configurable threshold of Ed25519 signatures before any transaction can be executed. It supports owner management (add/remove signers, change threshold), a transaction queue with expiry, and timelock enforcement on large transfers. This contract adapts and generalizes the proven multisig verification pattern from BridgeETH into a reusable wallet primitive suitable for DAOs, teams, treasuries, and any scenario requiring shared custody.

## Why It's Useful

- **Shared custody for organizations**: Teams, DAOs, and corporate treasuries need multi-party authorization for fund movements. A single compromised key should never be sufficient to drain funds.
- **Reduced single point of failure**: Even if one signer's private key is compromised, the attacker cannot move funds without colluding with additional signers up to the threshold.
- **Regulatory compliance**: Many institutional and enterprise use cases require multi-party approval workflows, particularly for large transfers. The timelock on high-value transactions adds an additional safety layer.
- **Flexible governance**: Owner sets can evolve over time as team members join or leave, and the threshold can be adjusted to match the current operational needs.
- **Phishing and social engineering resistance**: Requiring multiple independent approvals dramatically reduces the success rate of social engineering attacks.
- **Estate and succession planning**: Combined with the Dead Man's Switch contract, a multisig wallet can serve as a succession vehicle where family members or trustees hold keys.

## Key Features

- M-of-N Ed25519 signature verification, where M (threshold) and N (total signers) are configurable at deployment and adjustable post-deployment via signer consensus
- Transaction proposal queue: any signer can propose a transaction (native transfer, contract call, or signer management operation), which enters a pending state awaiting approvals
- Per-transaction approval tracking: each signer can approve or revoke their approval before execution
- Transaction expiry: proposals expire after a configurable number of blocks if not executed, preventing stale transactions from lingering
- Timelock on large transfers: transactions exceeding a configurable value threshold enter a mandatory delay period after reaching approval quorum, giving signers time to detect and cancel fraudulent proposals
- Owner management: proposals to add a new signer, remove an existing signer, or change the threshold M follow the same M-of-N approval flow
- Batch execution: execute multiple approved transactions in a single call to reduce gas overhead
- Nonce-based replay protection: each transaction proposal has a unique nonce that prevents double-execution
- Emergency pause: if threshold signers agree, the wallet can be paused to prevent all outgoing transactions
- View functions for inspecting pending transactions, approval status, signer list, and wallet balance

## Basalt-Specific Advantages

- **Native Ed25519 multisig verification**: Basalt uses Ed25519 as its primary signature scheme. The multisig wallet verifies Ed25519 signatures on-chain using the same `Ed25519Signer.Verify(PublicKey, message, Signature)` pattern proven in BridgeETH, with no need for ecrecover workarounds or precompile dependencies.
- **BLAKE3 transaction hashing**: Transaction proposals are hashed with BLAKE3 (Basalt's primary hash function), providing 256-bit security with extremely fast software performance, far outpacing keccak256 or SHA-256 in throughput.
- **Chain ID and contract address in hash domain**: Following the BridgeETH pattern (BRIDGE-01), transaction hashes include chain ID and contract address to prevent cross-chain and cross-contract replay attacks.
- **AOT-compiled execution**: The contract compiles ahead-of-time via .NET 9 Native AOT, delivering deterministic and efficient execution without JIT warmup, critical for a security-sensitive contract that must execute predictably.
- **ZK compliance integration**: The multisig wallet can optionally enforce ZK compliance checks on outgoing transfers by calling into the SchemaRegistry/IssuerRegistry, ensuring that even multisig-controlled funds respect regulatory constraints.
- **UInt256 native amounts**: All value fields use Basalt's native `UInt256` type, avoiding truncation issues common on chains that pass amounts as encoded calldata.
- **Cross-contract call support**: The wallet can execute arbitrary contract calls via `Context.CallContract<T>()`, enabling it to interact with any deployed Basalt contract (governance voting, staking, bridge operations) as a first-class actor.

## Token Standards Used

- **BST-20**: The multisig wallet can hold and transfer BST-20 fungible tokens via cross-contract calls
- **BST-721**: NFT custody and transfer support for multisig-controlled NFT collections
- **BST-3525**: Semi-fungible token operations (slot transfers, value splitting) can be proposed as multisig transactions

## Integration Points

- **BridgeETH (0x...1008)**: Reuses and generalizes BridgeETH's M-of-N Ed25519 signature verification pattern. The multisig wallet can also interact with BridgeETH to initiate cross-chain transfers under multisig control.
- **Governance (0x...1005 area)**: The multisig wallet can vote on governance proposals, delegate voting power, and execute governance actions on behalf of the signer set.
- **StakingPool (0x...1005)**: Idle funds in the multisig wallet can be staked via proposals to the StakingPool contract, and rewards can be claimed back.
- **Escrow (0x...1003)**: The multisig can create and manage escrow agreements, useful for team payment schedules.
- **BNS (0x...1002)**: The multisig wallet can register and manage Basalt Name Service names.
- **SchemaRegistry / IssuerRegistry**: Optional compliance verification on outgoing transfers.

## Technical Sketch

```csharp
using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Multi-Signature Wallet -- M-of-N Ed25519 signature verification for shared custody.
/// Generalizes BridgeETH's multisig pattern into a reusable wallet primitive.
/// </summary>
[BasaltContract]
public partial class MultisigWallet
{
    private const int PubKeySize = 32;
    private const int SigSize = 64;
    private const int SignatureEntrySize = PubKeySize + SigSize; // 96 bytes

    // --- Configuration ---
    private readonly StorageValue<uint> _threshold;
    private readonly StorageValue<uint> _signerCount;
    private readonly StorageMap<string, bool> _signers;           // pubKeyHex -> isSigner
    private readonly StorageValue<bool> _paused;

    // --- Timelock config ---
    private readonly StorageValue<UInt256> _timelockValueThreshold; // transfers above this require timelock
    private readonly StorageValue<ulong> _timelockDelayBlocks;      // blocks to wait after approval

    // --- Transaction queue ---
    private readonly StorageValue<ulong> _nextTxId;
    private readonly StorageMap<string, string> _txTypes;          // txId -> "transfer"|"call"|"addSigner"|"removeSigner"|"changeThreshold"
    private readonly StorageMap<string, string> _txRecipients;     // txId -> recipient hex
    private readonly StorageMap<string, UInt256> _txAmounts;       // txId -> amount
    private readonly StorageMap<string, string> _txCallTargets;    // txId -> contract address hex (for call type)
    private readonly StorageMap<string, string> _txCallMethods;    // txId -> method name (for call type)
    private readonly StorageMap<string, string> _txStatus;         // txId -> "pending"|"approved"|"timelocked"|"executed"|"cancelled"|"expired"
    private readonly StorageMap<string, uint> _txApprovalCount;    // txId -> count
    private readonly StorageMap<string, bool> _txApprovals;        // "txId:signerHex" -> approved
    private readonly StorageMap<string, ulong> _txExpiryBlocks;    // txId -> expiry block
    private readonly StorageMap<string, ulong> _txTimelockReady;   // txId -> block when timelock expires
    private readonly StorageMap<string, string> _txSignerPayload;  // txId -> new signer hex (for add/remove ops)
    private readonly StorageMap<string, uint> _txNewThreshold;     // txId -> new threshold (for changeThreshold)

    // --- Config ---
    private readonly StorageValue<ulong> _defaultExpiryBlocks;

    public MultisigWallet(uint threshold = 2, ulong defaultExpiryBlocks = 50400,
        UInt256 timelockValueThreshold = default, ulong timelockDelayBlocks = 7200)
    {
        _threshold = new StorageValue<uint>("msw_thresh");
        _signerCount = new StorageValue<uint>("msw_sigcnt");
        _signers = new StorageMap<string, bool>("msw_sig");
        _paused = new StorageValue<bool>("msw_paused");
        _timelockValueThreshold = new StorageValue<UInt256>("msw_tlval");
        _timelockDelayBlocks = new StorageValue<ulong>("msw_tldly");
        _nextTxId = new StorageValue<ulong>("msw_nxtx");
        _txTypes = new StorageMap<string, string>("msw_ttype");
        _txRecipients = new StorageMap<string, string>("msw_trec");
        _txAmounts = new StorageMap<string, UInt256>("msw_tamt");
        _txCallTargets = new StorageMap<string, string>("msw_tcall");
        _txCallMethods = new StorageMap<string, string>("msw_tmeth");
        _txStatus = new StorageMap<string, string>("msw_tsts");
        _txApprovalCount = new StorageMap<string, uint>("msw_tcnt");
        _txApprovals = new StorageMap<string, bool>("msw_tappr");
        _txExpiryBlocks = new StorageMap<string, ulong>("msw_texp");
        _txTimelockReady = new StorageMap<string, ulong>("msw_ttlr");
        _txSignerPayload = new StorageMap<string, string>("msw_tsig");
        _txNewThreshold = new StorageMap<string, uint>("msw_tnthr");
        _defaultExpiryBlocks = new StorageValue<ulong>("msw_defexp");

        Context.Require(threshold >= 1, "MSW: threshold must be >= 1");

        // Deployer is the first signer
        var deployerHex = Convert.ToHexString(Context.Caller);
        _signers.Set(deployerHex, true);
        _signerCount.Set(1);
        _threshold.Set(threshold);
        _defaultExpiryBlocks.Set(defaultExpiryBlocks);

        if (timelockValueThreshold.IsZero)
            timelockValueThreshold = new UInt256(1_000_000); // default 1M base units
        _timelockValueThreshold.Set(timelockValueThreshold);
        _timelockDelayBlocks.Set(timelockDelayBlocks);
    }

    // ===================== Signer Management Proposals =====================

    [BasaltEntrypoint]
    public ulong ProposeAddSigner(byte[] newSignerPublicKey)
    {
        RequireNotPaused();
        RequireSigner();
        Context.Require(newSignerPublicKey.Length == PubKeySize, "MSW: invalid public key length");
        var hex = Convert.ToHexString(newSignerPublicKey);
        Context.Require(!_signers.Get(hex), "MSW: already a signer");

        var txId = CreateTransaction("addSigner", [], UInt256.Zero);
        _txSignerPayload.Set(txId.ToString(), hex);

        Context.Emit(new MultisigTxProposedEvent
        {
            TxId = txId, TxType = "addSigner", Proposer = Context.Caller
        });
        return txId;
    }

    [BasaltEntrypoint]
    public ulong ProposeRemoveSigner(byte[] signerPublicKey)
    {
        RequireNotPaused();
        RequireSigner();
        var hex = Convert.ToHexString(signerPublicKey);
        Context.Require(_signers.Get(hex), "MSW: not a signer");
        Context.Require(_signerCount.Get() - 1 >= _threshold.Get(),
            "MSW: would go below threshold");

        var txId = CreateTransaction("removeSigner", [], UInt256.Zero);
        _txSignerPayload.Set(txId.ToString(), hex);

        Context.Emit(new MultisigTxProposedEvent
        {
            TxId = txId, TxType = "removeSigner", Proposer = Context.Caller
        });
        return txId;
    }

    [BasaltEntrypoint]
    public ulong ProposeChangeThreshold(uint newThreshold)
    {
        RequireNotPaused();
        RequireSigner();
        Context.Require(newThreshold >= 1 && newThreshold <= _signerCount.Get(),
            "MSW: invalid threshold");

        var txId = CreateTransaction("changeThreshold", [], UInt256.Zero);
        _txNewThreshold.Set(txId.ToString(), newThreshold);

        Context.Emit(new MultisigTxProposedEvent
        {
            TxId = txId, TxType = "changeThreshold", Proposer = Context.Caller
        });
        return txId;
    }

    // ===================== Transfer & Call Proposals =====================

    [BasaltEntrypoint]
    public ulong ProposeTransfer(byte[] recipient, UInt256 amount)
    {
        RequireNotPaused();
        RequireSigner();
        Context.Require(recipient.Length > 0, "MSW: invalid recipient");
        Context.Require(!amount.IsZero, "MSW: zero amount");

        var txId = CreateTransaction("transfer", recipient, amount);

        Context.Emit(new MultisigTxProposedEvent
        {
            TxId = txId, TxType = "transfer", Proposer = Context.Caller
        });
        return txId;
    }

    [BasaltEntrypoint]
    public ulong ProposeContractCall(byte[] targetContract, string methodName)
    {
        RequireNotPaused();
        RequireSigner();
        Context.Require(targetContract.Length > 0, "MSW: invalid target");
        Context.Require(!string.IsNullOrEmpty(methodName), "MSW: method required");

        var txId = CreateTransaction("call", [], UInt256.Zero);
        _txCallTargets.Set(txId.ToString(), Convert.ToHexString(targetContract));
        _txCallMethods.Set(txId.ToString(), methodName);

        Context.Emit(new MultisigTxProposedEvent
        {
            TxId = txId, TxType = "call", Proposer = Context.Caller
        });
        return txId;
    }

    // ===================== Approval & Execution =====================

    [BasaltEntrypoint]
    public void Approve(ulong txId)
    {
        RequireNotPaused();
        RequireSigner();
        var key = txId.ToString();
        var status = _txStatus.Get(key);
        Context.Require(status == "pending", "MSW: tx not pending");
        Context.Require(Context.BlockHeight <= _txExpiryBlocks.Get(key), "MSW: tx expired");

        var approvalKey = key + ":" + Convert.ToHexString(Context.Caller);
        Context.Require(!_txApprovals.Get(approvalKey), "MSW: already approved");

        _txApprovals.Set(approvalKey, true);
        var newCount = _txApprovalCount.Get(key) + 1;
        _txApprovalCount.Set(key, newCount);

        Context.Emit(new MultisigApprovalEvent
        {
            TxId = txId, Signer = Context.Caller, ApprovalCount = newCount
        });

        // Auto-advance to approved/timelocked if threshold reached
        if (newCount >= _threshold.Get())
        {
            var amount = _txAmounts.Get(key);
            if (amount > _timelockValueThreshold.Get())
            {
                _txStatus.Set(key, "timelocked");
                var readyBlock = Context.BlockHeight + _timelockDelayBlocks.Get();
                _txTimelockReady.Set(key, readyBlock);

                Context.Emit(new MultisigTimelockStartedEvent
                {
                    TxId = txId, ReadyBlock = readyBlock
                });
            }
            else
            {
                _txStatus.Set(key, "approved");
            }
        }
    }

    [BasaltEntrypoint]
    public void RevokeApproval(ulong txId)
    {
        RequireSigner();
        var key = txId.ToString();
        var status = _txStatus.Get(key);
        Context.Require(status == "pending" || status == "timelocked", "MSW: cannot revoke");

        var approvalKey = key + ":" + Convert.ToHexString(Context.Caller);
        Context.Require(_txApprovals.Get(approvalKey), "MSW: not approved");

        _txApprovals.Set(approvalKey, false);
        var newCount = _txApprovalCount.Get(key) - 1;
        _txApprovalCount.Set(key, newCount);

        // Revert to pending if below threshold
        if (newCount < _threshold.Get())
            _txStatus.Set(key, "pending");

        Context.Emit(new MultisigApprovalRevokedEvent
        {
            TxId = txId, Signer = Context.Caller, ApprovalCount = newCount
        });
    }

    [BasaltEntrypoint]
    public void Execute(ulong txId)
    {
        RequireNotPaused();
        RequireSigner();
        var key = txId.ToString();
        var status = _txStatus.Get(key);

        // Must be approved or timelocked-and-ready
        if (status == "timelocked")
        {
            Context.Require(Context.BlockHeight >= _txTimelockReady.Get(key),
                "MSW: timelock not expired");
        }
        else
        {
            Context.Require(status == "approved", "MSW: tx not ready");
        }

        Context.Require(Context.BlockHeight <= _txExpiryBlocks.Get(key), "MSW: tx expired");

        _txStatus.Set(key, "executed");
        var txType = _txTypes.Get(key);

        if (txType == "transfer")
        {
            var recipient = Convert.FromHexString(_txRecipients.Get(key));
            var amount = _txAmounts.Get(key);
            Context.TransferNative(recipient, amount);
        }
        else if (txType == "call")
        {
            var target = Convert.FromHexString(_txCallTargets.Get(key));
            var method = _txCallMethods.Get(key);
            Context.CallContract(target, method);
        }
        else if (txType == "addSigner")
        {
            var signerHex = _txSignerPayload.Get(key);
            _signers.Set(signerHex, true);
            _signerCount.Set(_signerCount.Get() + 1);
        }
        else if (txType == "removeSigner")
        {
            var signerHex = _txSignerPayload.Get(key);
            _signers.Set(signerHex, false);
            _signerCount.Set(_signerCount.Get() - 1);
        }
        else if (txType == "changeThreshold")
        {
            var newThreshold = _txNewThreshold.Get(key);
            _threshold.Set(newThreshold);
        }

        Context.Emit(new MultisigTxExecutedEvent { TxId = txId, TxType = txType });
    }

    [BasaltEntrypoint]
    public void Cancel(ulong txId)
    {
        RequireSigner();
        var key = txId.ToString();
        var status = _txStatus.Get(key);
        Context.Require(status == "pending" || status == "approved" || status == "timelocked",
            "MSW: cannot cancel");

        _txStatus.Set(key, "cancelled");

        Context.Emit(new MultisigTxCancelledEvent { TxId = txId });
    }

    // ===================== Batch Execution (Off-Chain Signatures) =====================

    /// <summary>
    /// Execute a transfer with packed Ed25519 signatures (BridgeETH pattern).
    /// Signatures are N x 96 bytes: [32B pubkey][64B sig] repeated.
    /// Message = BLAKE3(version || chainId || contractAddr || LE_u64(nonce) || recipient || LE_u256(amount))
    /// </summary>
    [BasaltEntrypoint]
    public void ExecuteWithSignatures(ulong nonce, byte[] recipient, UInt256 amount, byte[] signatures)
    {
        RequireNotPaused();
        Context.Require(recipient.Length == 20, "MSW: recipient must be 20 bytes");
        Context.Require(!amount.IsZero, "MSW: zero amount");
        Context.Require(signatures.Length > 0 && signatures.Length % SignatureEntrySize == 0,
            "MSW: invalid signatures format");

        var messageHash = ComputeTransactionHash(nonce, recipient, amount);
        var threshold = _threshold.Get();
        uint validCount = 0;
        var seen = new HashSet<string>();

        for (var i = 0; i < signatures.Length / SignatureEntrySize; i++)
        {
            var offset = i * SignatureEntrySize;
            var pubKeyBytes = signatures[offset..(offset + PubKeySize)];
            var sigBytes = signatures[(offset + PubKeySize)..(offset + SignatureEntrySize)];

            var pubKeyHex = Convert.ToHexString(pubKeyBytes);
            if (!_signers.Get(pubKeyHex)) continue;
            if (!seen.Add(pubKeyHex)) continue;

            var pubKey = new PublicKey(pubKeyBytes);
            var sig = new Signature(sigBytes);
            if (Ed25519Signer.Verify(pubKey, messageHash, sig))
                validCount++;

            if (validCount >= threshold) break;
        }

        Context.Require(validCount >= threshold, "MSW: insufficient valid signatures");

        Context.TransferNative(recipient, amount);

        Context.Emit(new MultisigDirectExecutionEvent
        {
            Nonce = nonce, Recipient = recipient, Amount = amount
        });
    }

    // ===================== Pause =====================

    [BasaltEntrypoint]
    public ulong ProposePause()
    {
        RequireSigner();
        var txId = CreateTransaction("pause", [], UInt256.Zero);
        Context.Emit(new MultisigTxProposedEvent
        {
            TxId = txId, TxType = "pause", Proposer = Context.Caller
        });
        return txId;
    }

    // ===================== Views =====================

    [BasaltView]
    public uint GetThreshold() => _threshold.Get();

    [BasaltView]
    public uint GetSignerCount() => _signerCount.Get();

    [BasaltView]
    public bool IsSigner(byte[] publicKey) => _signers.Get(Convert.ToHexString(publicKey));

    [BasaltView]
    public string GetTransactionStatus(ulong txId) => _txStatus.Get(txId.ToString()) ?? "unknown";

    [BasaltView]
    public uint GetApprovalCount(ulong txId) => _txApprovalCount.Get(txId.ToString());

    [BasaltView]
    public bool HasApproved(ulong txId, byte[] signer)
        => _txApprovals.Get(txId.ToString() + ":" + Convert.ToHexString(signer));

    [BasaltView]
    public ulong GetExpiryBlock(ulong txId) => _txExpiryBlocks.Get(txId.ToString());

    [BasaltView]
    public ulong GetTimelockReadyBlock(ulong txId) => _txTimelockReady.Get(txId.ToString());

    [BasaltView]
    public bool IsPaused() => _paused.Get();

    // ===================== Internal Helpers =====================

    private ulong CreateTransaction(string txType, byte[] recipient, UInt256 amount)
    {
        var txId = _nextTxId.Get();
        _nextTxId.Set(txId + 1);

        var key = txId.ToString();
        _txTypes.Set(key, txType);
        _txRecipients.Set(key, Convert.ToHexString(recipient));
        _txAmounts.Set(key, amount);
        _txStatus.Set(key, "pending");
        _txApprovalCount.Set(key, 0);
        _txExpiryBlocks.Set(key, Context.BlockHeight + _defaultExpiryBlocks.Get());

        return txId;
    }

    private void RequireSigner()
    {
        Context.Require(_signers.Get(Convert.ToHexString(Context.Caller)), "MSW: not a signer");
    }

    private void RequireNotPaused()
    {
        Context.Require(!_paused.Get(), "MSW: paused");
    }

    private static byte[] ComputeTransactionHash(ulong nonce, byte[] recipient, UInt256 amount)
    {
        // version(1) + chainId(4) + contractAddress(20) + nonce(8) + recipient(20) + amount(32) = 85
        var data = new byte[1 + 4 + 20 + 8 + 20 + 32];
        var offset = 0;

        data[offset] = 0x01; // version
        offset += 1;
        BitConverter.TryWriteBytes(data.AsSpan(offset, 4), Context.ChainId);
        offset += 4;
        Context.Self.CopyTo(data.AsSpan(offset, 20));
        offset += 20;
        BitConverter.TryWriteBytes(data.AsSpan(offset, 8), nonce);
        offset += 8;
        recipient.CopyTo(data.AsSpan(offset, 20));
        offset += 20;
        amount.WriteTo(data.AsSpan(offset, 32));

        return Blake3Hasher.Hash(data).ToArray();
    }
}

// ===================== Events =====================

[BasaltEvent]
public class MultisigTxProposedEvent
{
    [Indexed] public ulong TxId { get; set; }
    public string TxType { get; set; } = "";
    [Indexed] public byte[] Proposer { get; set; } = null!;
}

[BasaltEvent]
public class MultisigApprovalEvent
{
    [Indexed] public ulong TxId { get; set; }
    [Indexed] public byte[] Signer { get; set; } = null!;
    public uint ApprovalCount { get; set; }
}

[BasaltEvent]
public class MultisigApprovalRevokedEvent
{
    [Indexed] public ulong TxId { get; set; }
    [Indexed] public byte[] Signer { get; set; } = null!;
    public uint ApprovalCount { get; set; }
}

[BasaltEvent]
public class MultisigTxExecutedEvent
{
    [Indexed] public ulong TxId { get; set; }
    public string TxType { get; set; } = "";
}

[BasaltEvent]
public class MultisigTxCancelledEvent
{
    [Indexed] public ulong TxId { get; set; }
}

[BasaltEvent]
public class MultisigTimelockStartedEvent
{
    [Indexed] public ulong TxId { get; set; }
    public ulong ReadyBlock { get; set; }
}

[BasaltEvent]
public class MultisigDirectExecutionEvent
{
    [Indexed] public ulong Nonce { get; set; }
    [Indexed] public byte[] Recipient { get; set; } = null!;
    public UInt256 Amount { get; set; }
}
```

## Complexity

**Medium** -- The core M-of-N signature verification logic is already proven in BridgeETH and can be adapted directly. The transaction queue, approval tracking, and timelock enforcement add moderate state management complexity, but each subcomponent is straightforward. No complex mathematical operations or novel cryptographic constructions are required.

## Priority

**P0** -- Multi-signature wallets are foundational infrastructure for any blockchain ecosystem. DAOs, teams, treasuries, and protocol-controlled funds all require multisig custody. This contract is a prerequisite for secure deployment of many other contracts (DAO Treasury, Contract Factory governance, etc.) and should be among the first contracts deployed on mainnet.
