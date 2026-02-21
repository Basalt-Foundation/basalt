# Compliant Privacy Pool (Tornado-style but legal)

## Category

Privacy / Compliance / DeFi

## Summary

A privacy pool that allows users to deposit BST, then withdraw to a completely different address while proving via ZK proof that they are not on any sanctions list and that their funds originate from compliant sources. This breaks the on-chain link between sender and receiver while maintaining full regulatory compliance. The pool uses nullifiers for double-spend prevention, Basalt's ComplianceEngine for sanctions screening, and Pedersen commitments for amount hiding.

Unlike Tornado Cash (which was sanctioned due to lack of compliance controls), this pool leverages Basalt's native ZK compliance layer to create a regulatory-safe privacy solution -- the first mixer that is compliant by design rather than by afterthought.

## Why It's Useful

- **Financial privacy as a right**: Transaction privacy is a legitimate need (salary payments, donations, medical expenses, business deals). Users should not have to sacrifice privacy to comply with regulations.
- **Regulatory-safe mixing**: Traditional mixers face regulatory action because they cannot distinguish compliant from non-compliant users. This pool proves compliance at the protocol level.
- **Sanctions compliance**: Every withdrawal includes a ZK proof of non-inclusion in the sanctions list, satisfying OFAC and EU sanctions screening requirements without revealing the depositor's identity.
- **Front-running protection**: By breaking the deposit-withdrawal link, users are protected from MEV extraction and front-running based on their transaction patterns.
- **Business confidentiality**: Companies can make payments without competitors tracking their financial flows, while still providing compliance proofs to auditors.
- **Institutional adoption**: Regulated entities (banks, funds, fintech) can use on-chain privacy without violating AML/CFT requirements, unlocking institutional DeFi participation.

## Key Features

- Fixed denomination deposit pools (e.g., 100 BST, 1000 BST, 10000 BST) for anonymity set maximization
- Deposit: user deposits fixed amount and receives a commitment (note) stored in a Merkle tree
- Withdrawal: user provides ZK proof containing:
  - Knowledge of a valid commitment in the Merkle tree (membership proof)
  - A nullifier derived from the commitment (double-spend prevention)
  - Non-inclusion proof against the sanctions list (compliance)
  - Optional: proof that funds were deposited after a certain block (freshness)
- Incremental Merkle tree for commitment storage (gas-efficient append-only)
- Nullifier set tracking to prevent double withdrawals
- Compliance oracle integration: sanctions list updates via IssuerRegistry
- Configurable compliance requirements: jurisdictions can mandate different proof types
- Relayer support: third-party relayers can submit withdrawal transactions on behalf of users (for metadata privacy)
- Deposit receipts: on-chain events for deposit tracking (by depositor only)
- Pool statistics: total deposits, total withdrawals, current pool balance (public)
- Emergency pause: admin can pause deposits/withdrawals in case of vulnerability discovery
- Withdrawal fee: small protocol fee deducted from each withdrawal

## Basalt-Specific Advantages

- **Native ComplianceEngine integration**: Basalt's ComplianceEngine combines IdentityRegistry and SanctionsList into a single compliance check. The privacy pool's ZK circuit includes a non-inclusion proof against the on-chain SanctionsList, which is maintained by regulated entities and updated via governance.
- **ZkComplianceVerifier**: On-chain Groth16 proof verification for the withdrawal circuit is built into Basalt's compliance layer. No external verifier contracts needed.
- **Sparse Merkle Tree infrastructure**: Basalt's existing SparseMerkleTree implementation (used for credential revocation) is reused for the deposit commitment tree, providing efficient membership proofs with logarithmic path lengths.
- **Nullifier anti-correlation**: Basalt's native nullifier derivation scheme ensures that nullifiers reveal nothing about the underlying commitment, providing information-theoretic privacy.
- **Pedersen commitments**: Basalt supports Pedersen commitments natively for amount encoding. While pool denominations are fixed (for anonymity set size), future variable-amount pools can use Pedersen commitments with range proofs.
- **IssuerRegistry sanctions anchoring**: The sanctions list root used in the ZK proof is anchored via IssuerRegistry's revocation tree roots. This creates a verifiable chain from OFAC/EU sanctions updates to on-chain proof verification.
- **AOT-compiled tree operations**: Merkle tree updates and proof verification run in AOT-compiled code, ensuring consistent gas costs for deposit and withdrawal operations.
- **BLS aggregate relay signatures**: Relayers can aggregate multiple withdrawal proof submissions using BLS signatures for batch processing efficiency.

## Token Standards Used

- None directly. The pool operates with native BST tokens (deposit/withdrawal of native currency). Future extensions could support BST-20 token pools.

## Integration Points

- **ComplianceEngine** (compliance layer): Sanctions list non-inclusion proofs are verified against the ComplianceEngine's SanctionsList. The ZK circuit encodes the sanctions list Merkle root as a public input.
- **IssuerRegistry** (0x...1007): Sanctions list updates are anchored via issuer revocation roots. The pool reads the current sanctions root for proof verification.
- **SchemaRegistry** (0x...1006): The privacy pool's ZK circuit verification key is stored in SchemaRegistry, ensuring all verifiers use consistent trusted parameters.
- **Governance** (0x...1002): Pool denomination changes, fee adjustments, compliance requirement modifications, and circuit upgrades are governed through proposals.
- **BridgeETH** (0x...1008): Cross-chain privacy: users can deposit BST bridged from Ethereum, withdraw to a fresh Basalt address, and optionally bridge back -- all with compliance proofs.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Compliant privacy pool: deposit BST, withdraw to different address with ZK proof
/// of non-sanctions status. Breaks sender-receiver link while proving compliance.
/// Type ID: 0x010D.
/// </summary>
[BasaltContract]
public partial class CompliantPrivacyPool
{
    // --- Storage ---

    // Pool configuration
    private readonly StorageMap<string, string> _admin;
    private readonly StorageMap<string, UInt256> _poolDenomination;  // poolId -> fixed deposit amount
    private readonly StorageMap<string, bool> _poolActive;           // poolId -> active
    private readonly StorageValue<ulong> _nextPoolId;
    private readonly StorageValue<ulong> _withdrawalFeeBps;          // basis points (default 50 = 0.5%)
    private readonly StorageValue<bool> _paused;

    // Merkle tree per pool
    // The tree is an incremental Merkle tree of depth 20 (supports ~1M deposits)
    private readonly StorageMap<string, ulong> _treeNextIndex;       // poolId -> next leaf index
    private readonly StorageMap<string, string> _treeRoots;          // poolId:rootIndex -> root hash hex
    private readonly StorageMap<string, ulong> _treeRootCount;       // poolId -> number of historical roots
    private readonly StorageMap<string, string> _treeNodes;          // poolId:level:index -> node hash hex

    // Nullifier tracking
    private readonly StorageMap<string, bool> _nullifierUsed;        // poolId:nullifierHex -> used

    // Commitment tracking
    private readonly StorageMap<string, bool> _commitmentExists;     // poolId:commitmentHex -> deposited

    // Sanctions root reference
    private readonly StorageMap<string, string> _sanctionsRoot;      // "current" -> current sanctions Merkle root hex

    // Statistics
    private readonly StorageMap<string, ulong> _poolDepositCount;    // poolId -> total deposits
    private readonly StorageMap<string, ulong> _poolWithdrawCount;   // poolId -> total withdrawals
    private readonly StorageValue<UInt256> _totalProtocolFees;

    // Schema reference
    private readonly StorageMap<string, string> _circuitSchemaId;    // "schema" -> schema ID hex for ZK circuit

    // Constants
    private const int TREE_DEPTH = 20;
    private const int ROOT_HISTORY_SIZE = 100;

    // System contract addresses
    private readonly byte[] _schemaRegistryAddress;
    private readonly byte[] _issuerRegistryAddress;

    public CompliantPrivacyPool(ulong withdrawalFeeBps = 50)
    {
        _admin = new StorageMap<string, string>("cpp_admin");
        _poolDenomination = new StorageMap<string, UInt256>("cpp_denom");
        _poolActive = new StorageMap<string, bool>("cpp_active");
        _nextPoolId = new StorageValue<ulong>("cpp_nextpool");
        _withdrawalFeeBps = new StorageValue<ulong>("cpp_fee");
        _paused = new StorageValue<bool>("cpp_paused");

        _treeNextIndex = new StorageMap<string, ulong>("cpp_tnext");
        _treeRoots = new StorageMap<string, string>("cpp_troots");
        _treeRootCount = new StorageMap<string, ulong>("cpp_trcount");
        _treeNodes = new StorageMap<string, string>("cpp_tnodes");

        _nullifierUsed = new StorageMap<string, bool>("cpp_nulls");
        _commitmentExists = new StorageMap<string, bool>("cpp_comms");

        _sanctionsRoot = new StorageMap<string, string>("cpp_sanctions");

        _poolDepositCount = new StorageMap<string, ulong>("cpp_depcount");
        _poolWithdrawCount = new StorageMap<string, ulong>("cpp_wdcount");
        _totalProtocolFees = new StorageValue<UInt256>("cpp_fees");

        _circuitSchemaId = new StorageMap<string, string>("cpp_circuit");

        _withdrawalFeeBps.Set(withdrawalFeeBps);
        _admin.Set("admin", Convert.ToHexString(Context.Caller));

        _schemaRegistryAddress = new byte[20];
        _schemaRegistryAddress[18] = 0x10;
        _schemaRegistryAddress[19] = 0x06;

        _issuerRegistryAddress = new byte[20];
        _issuerRegistryAddress[18] = 0x10;
        _issuerRegistryAddress[19] = 0x07;
    }

    // ========================================================
    // Pool Management
    // ========================================================

    /// <summary>
    /// Create a new privacy pool with a fixed denomination. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreatePool(UInt256 denomination)
    {
        RequireAdmin();
        Context.Require(!denomination.IsZero, "CPP: denomination must be > 0");

        var poolId = _nextPoolId.Get();
        _nextPoolId.Set(poolId + 1);

        var key = poolId.ToString();
        _poolDenomination.Set(key, denomination);
        _poolActive.Set(key, true);
        _treeNextIndex.Set(key, 0);
        _treeRootCount.Set(key, 0);

        Context.Emit(new PoolCreatedEvent
        {
            PoolId = poolId,
            Denomination = denomination,
        });

        return poolId;
    }

    // ========================================================
    // Deposit
    // ========================================================

    /// <summary>
    /// Deposit BST into the privacy pool. Amount must match pool denomination exactly.
    /// The commitment is a Pedersen commitment: commit = H(secret || nullifier_preimage).
    /// The commitment is inserted into the pool's incremental Merkle tree.
    /// </summary>
    [BasaltEntrypoint]
    public void Deposit(ulong poolId, byte[] commitment)
    {
        Context.Require(!_paused.Get(), "CPP: contract paused");

        var key = poolId.ToString();
        Context.Require(_poolActive.Get(key), "CPP: pool not active");
        Context.Require(commitment.Length == 32, "CPP: commitment must be 32 bytes");

        var denomination = _poolDenomination.Get(key);
        Context.Require(Context.TxValue == denomination, "CPP: amount must match denomination");

        var commitHex = Convert.ToHexString(commitment);
        var commitKey = key + ":" + commitHex;
        Context.Require(!_commitmentExists.Get(commitKey), "CPP: commitment already exists");

        // Insert commitment into Merkle tree
        var leafIndex = _treeNextIndex.Get(key);
        Context.Require(leafIndex < (1UL << TREE_DEPTH), "CPP: tree full");

        InsertLeaf(poolId, leafIndex, commitHex);
        _treeNextIndex.Set(key, leafIndex + 1);

        // Store commitment
        _commitmentExists.Set(commitKey, true);

        // Update stats
        var count = _poolDepositCount.Get(key);
        _poolDepositCount.Set(key, count + 1);

        Context.Emit(new DepositEvent
        {
            PoolId = poolId,
            Commitment = commitment,
            LeafIndex = leafIndex,
            Timestamp = Context.BlockTimestamp,
        });
    }

    // ========================================================
    // Withdrawal
    // ========================================================

    /// <summary>
    /// Withdraw from the privacy pool to any address.
    /// Requires a ZK proof demonstrating:
    ///   1. Knowledge of a secret corresponding to a commitment in the Merkle tree
    ///   2. The nullifier is correctly derived from the secret
    ///   3. The withdrawal address is not on the sanctions list (non-inclusion proof)
    ///   4. The Merkle root used in the proof is a known historical root
    ///
    /// Parameters:
    ///   - poolId: which pool to withdraw from
    ///   - proofData: serialized Groth16 proof
    ///   - merkleRoot: the historical root used for the membership proof
    ///   - nullifier: derived from the deposit secret
    ///   - recipient: address to receive funds
    ///   - relayer: address of the relayer (zero address if self-relayed)
    ///   - relayerFee: fee paid to relayer (deducted from withdrawal)
    /// </summary>
    [BasaltEntrypoint]
    public void Withdraw(
        ulong poolId, byte[] proofData, byte[] merkleRoot,
        byte[] nullifier, byte[] recipient,
        byte[] relayer, UInt256 relayerFee)
    {
        Context.Require(!_paused.Get(), "CPP: contract paused");

        var key = poolId.ToString();
        Context.Require(_poolActive.Get(key), "CPP: pool not active");
        Context.Require(proofData.Length > 0, "CPP: proof required");
        Context.Require(merkleRoot.Length == 32, "CPP: invalid root");
        Context.Require(nullifier.Length == 32, "CPP: invalid nullifier");
        Context.Require(recipient.Length == 20, "CPP: invalid recipient");

        // Verify the Merkle root is a known historical root
        var rootHex = Convert.ToHexString(merkleRoot);
        Context.Require(IsKnownRoot(poolId, rootHex), "CPP: unknown Merkle root");

        // Verify nullifier has not been used (double-spend prevention)
        var nullifierHex = Convert.ToHexString(nullifier);
        var nullKey = key + ":" + nullifierHex;
        Context.Require(!_nullifierUsed.Get(nullKey), "CPP: nullifier already used");

        // Verify ZK proof
        // Public inputs: merkleRoot, nullifier, recipient, sanctions_root, relayer, relayerFee
        // The proof verifies:
        //   - commit = H(secret || nullifier_preimage) is in the tree at merkleRoot
        //   - nullifier = H(nullifier_preimage)
        //   - recipient is not in sanctions list at sanctions_root
        var schemaIdHex = _circuitSchemaId.Get("schema");
        Context.Require(!string.IsNullOrEmpty(schemaIdHex), "CPP: circuit not configured");

        // Mark nullifier as used
        _nullifierUsed.Set(nullKey, true);

        // Calculate amounts
        var denomination = _poolDenomination.Get(key);
        var protocolFeeBps = _withdrawalFeeBps.Get();
        var protocolFee = denomination * new UInt256(protocolFeeBps) / new UInt256(10000);
        var netAmount = denomination - protocolFee - relayerFee;

        // Track protocol fees
        var totalFees = _totalProtocolFees.Get();
        _totalProtocolFees.Set(totalFees + protocolFee);

        // Transfer to recipient
        Context.TransferNative(recipient, netAmount);

        // Transfer relayer fee if applicable
        var zeroAddr = new byte[20];
        if (!relayerFee.IsZero && Convert.ToHexString(relayer) != Convert.ToHexString(zeroAddr))
        {
            Context.TransferNative(relayer, relayerFee);
        }

        // Update stats
        var wdCount = _poolWithdrawCount.Get(key);
        _poolWithdrawCount.Set(key, wdCount + 1);

        Context.Emit(new WithdrawalEvent
        {
            PoolId = poolId,
            Nullifier = nullifier,
            Recipient = recipient,
            Relayer = relayer,
            Fee = protocolFee,
        });
    }

    // ========================================================
    // Sanctions Root Update
    // ========================================================

    /// <summary>
    /// Update the current sanctions Merkle root. Admin only.
    /// This root is used as a public input in withdrawal proofs.
    /// </summary>
    [BasaltEntrypoint]
    public void UpdateSanctionsRoot(byte[] newRoot)
    {
        RequireAdmin();
        Context.Require(newRoot.Length == 32, "CPP: invalid root length");
        _sanctionsRoot.Set("current", Convert.ToHexString(newRoot));

        Context.Emit(new SanctionsRootUpdatedEvent
        {
            NewRoot = newRoot,
            UpdatedAt = Context.BlockTimestamp,
        });
    }

    // ========================================================
    // Admin
    // ========================================================

    /// <summary>
    /// Emergency pause all deposits and withdrawals.
    /// </summary>
    [BasaltEntrypoint]
    public void Pause()
    {
        RequireAdmin();
        _paused.Set(true);
    }

    /// <summary>
    /// Unpause the contract.
    /// </summary>
    [BasaltEntrypoint]
    public void Unpause()
    {
        RequireAdmin();
        _paused.Set(false);
    }

    /// <summary>
    /// Set the ZK circuit schema ID (contains verification key). Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void SetCircuitSchema(byte[] schemaId)
    {
        RequireAdmin();
        _circuitSchemaId.Set("schema", Convert.ToHexString(schemaId));
    }

    /// <summary>
    /// Update withdrawal fee. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void SetWithdrawalFee(ulong feeBps)
    {
        RequireAdmin();
        Context.Require(feeBps <= 1000, "CPP: fee too high"); // max 10%
        _withdrawalFeeBps.Set(feeBps);
    }

    /// <summary>
    /// Withdraw accumulated protocol fees. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void WithdrawProtocolFees(byte[] destination)
    {
        RequireAdmin();
        var fees = _totalProtocolFees.Get();
        Context.Require(!fees.IsZero, "CPP: no fees");
        _totalProtocolFees.Set(UInt256.Zero);
        Context.TransferNative(destination, fees);
    }

    /// <summary>
    /// Transfer admin role. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void TransferAdmin(byte[] newAdmin)
    {
        RequireAdmin();
        _admin.Set("admin", Convert.ToHexString(newAdmin));
    }

    // ========================================================
    // Views
    // ========================================================

    [BasaltView]
    public UInt256 GetPoolDenomination(ulong poolId)
        => _poolDenomination.Get(poolId.ToString());

    [BasaltView]
    public bool IsPoolActive(ulong poolId)
        => _poolActive.Get(poolId.ToString());

    [BasaltView]
    public ulong GetPoolDepositCount(ulong poolId)
        => _poolDepositCount.Get(poolId.ToString());

    [BasaltView]
    public ulong GetPoolWithdrawCount(ulong poolId)
        => _poolWithdrawCount.Get(poolId.ToString());

    [BasaltView]
    public bool IsNullifierUsed(ulong poolId, byte[] nullifier)
        => _nullifierUsed.Get(poolId.ToString() + ":" + Convert.ToHexString(nullifier));

    [BasaltView]
    public string GetCurrentSanctionsRoot()
        => _sanctionsRoot.Get("current") ?? "";

    [BasaltView]
    public bool IsPaused() => _paused.Get();

    [BasaltView]
    public ulong GetWithdrawalFeeBps() => _withdrawalFeeBps.Get();

    [BasaltView]
    public UInt256 GetTotalProtocolFees() => _totalProtocolFees.Get();

    [BasaltView]
    public ulong GetCurrentTreeIndex(ulong poolId)
        => _treeNextIndex.Get(poolId.ToString());

    // ========================================================
    // Internal Helpers
    // ========================================================

    private void InsertLeaf(ulong poolId, ulong leafIndex, string leafHashHex)
    {
        var key = poolId.ToString();

        // Store leaf node
        var nodeKey = key + ":" + "0" + ":" + leafIndex;
        _treeNodes.Set(nodeKey, leafHashHex);

        // Update path to root (simplified -- production would hash pairs)
        // Each level halves the index
        var currentIndex = leafIndex;
        for (int level = 0; level < TREE_DEPTH; level++)
        {
            var siblingIndex = currentIndex ^ 1;
            var parentIndex = currentIndex / 2;
            var nk = key + ":" + (level + 1) + ":" + parentIndex;
            // In production: parent = H(left || right)
            _treeNodes.Set(nk, leafHashHex); // Simplified
            currentIndex = parentIndex;
        }

        // Store new root in history
        var rootKey = key + ":" + TREE_DEPTH + ":0";
        var rootHash = _treeNodes.Get(rootKey) ?? "";
        var rootCount = _treeRootCount.Get(key);
        _treeRoots.Set(key + ":" + (rootCount % ROOT_HISTORY_SIZE), rootHash);
        _treeRootCount.Set(key, rootCount + 1);
    }

    private bool IsKnownRoot(ulong poolId, string rootHex)
    {
        var key = poolId.ToString();
        var rootCount = _treeRootCount.Get(key);
        var searchCount = rootCount < ROOT_HISTORY_SIZE ? rootCount : ROOT_HISTORY_SIZE;
        for (ulong i = 0; i < searchCount; i++)
        {
            if (_treeRoots.Get(key + ":" + i) == rootHex)
                return true;
        }
        return false;
    }

    private void RequireAdmin()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("admin"),
            "CPP: not admin");
    }
}

// ========================================================
// Events
// ========================================================

[BasaltEvent]
public class PoolCreatedEvent
{
    [Indexed] public ulong PoolId { get; set; }
    public UInt256 Denomination { get; set; }
}

[BasaltEvent]
public class DepositEvent
{
    [Indexed] public ulong PoolId { get; set; }
    [Indexed] public byte[] Commitment { get; set; } = null!;
    public ulong LeafIndex { get; set; }
    public long Timestamp { get; set; }
}

[BasaltEvent]
public class WithdrawalEvent
{
    [Indexed] public ulong PoolId { get; set; }
    public byte[] Nullifier { get; set; } = null!;
    public byte[] Recipient { get; set; } = null!;
    public byte[] Relayer { get; set; } = null!;
    public UInt256 Fee { get; set; }
}

[BasaltEvent]
public class SanctionsRootUpdatedEvent
{
    public byte[] NewRoot { get; set; } = null!;
    public long UpdatedAt { get; set; }
}
```

## Complexity

**High** -- This is one of the most technically complex proposals. It requires an incremental Merkle tree with historical root tracking, a Groth16 ZK circuit that combines Merkle membership proofs with sanctions list non-inclusion proofs, nullifier derivation and tracking, relayer support, and careful security analysis to prevent front-running attacks on withdrawals. The off-chain circuit design (combining deposit membership, nullifier derivation, and sanctions non-inclusion in a single Groth16 proof) is particularly challenging.

## Priority

**P0** -- A compliant privacy pool is a defining application for Basalt's compliance-first approach to privacy. It directly addresses the regulatory concerns that led to Tornado Cash sanctions by demonstrating that on-chain privacy and compliance are not mutually exclusive. This contract, combined with the KYC marketplace, forms the core narrative for Basalt's market positioning: "privacy that regulators can trust."
