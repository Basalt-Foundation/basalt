# Private Payroll System

## Category

Privacy / Enterprise / DeFi

## Summary

A privacy-preserving payroll system where employers deposit a lump sum, and employees claim their individual salary amounts using ZK proofs of employment (backed by BST-VC credentials) without revealing individual salary amounts to anyone except the employer and the specific employee. Pedersen commitments hide individual payment amounts while a ZK proof ensures the total of all claims exactly matches the registered payroll total, maintaining accounting integrity.

This enables on-chain payroll processing that provides the transparency auditors need (total payroll is correct) while preserving the salary confidentiality that employees expect.

## Why It's Useful

- **Salary confidentiality**: Employee salaries are sensitive information. Public blockchains expose all transaction amounts, making on-chain payroll impractical for most organizations. This contract solves that problem.
- **Accounting integrity**: The ZK proof that individual claims sum to the registered total ensures the employer cannot over- or under-pay without detection, providing auditable correctness without revealing individual amounts.
- **Reduced administration**: Payroll processing, tax withholding calculations, and disbursement are automated on-chain, reducing administrative overhead and errors.
- **Global payroll**: Remote and distributed teams can be paid in BST regardless of jurisdiction, with compliance proofs satisfying local regulatory requirements.
- **Employee control**: Employees claim their own salary using their BST-VC employment credential, eliminating dependency on centralized payroll processors.
- **Audit trail**: On-chain records provide a permanent, tamper-proof audit trail of payroll operations for regulatory compliance, without exposing individual salary data.

## Key Features

- Employer registers a payroll batch with a Pedersen commitment to the total amount and individual employee commitments
- Employees claim salaries by providing a ZK proof that their BST-VC employment credential is valid and their claimed amount matches their individual commitment
- Sum verification: ZK proof that the sum of all individual commitments equals the total commitment (homomorphic property of Pedersen commitments)
- BST-VC employment credentials: each employee holds a verifiable credential issued by the employer confirming employment status and salary commitment
- Payroll period management: monthly, bi-weekly, or custom pay periods
- Partial claim support: employees can claim a portion of their salary (e.g., on-demand pay)
- Tax withholding: employer can configure jurisdiction-specific withholding rates
- Multi-currency support: salaries denominated in BST or BST-20 stablecoins
- Employer deposit verification: ensure total deposit matches committed payroll total before enabling claims
- Late claim penalties: configurable penalty for claims after the pay period deadline
- Employee roster management: add/remove employees, update salary commitments between pay periods
- Audit mode: designated auditor can verify individual claims against commitments (with employer authorization)
- Emergency payout: admin can trigger direct payouts if ZK system is unavailable

## Basalt-Specific Advantages

- **Pedersen commitment homomorphism**: Basalt's native Pedersen commitment support enables the core property that makes private payroll work: `C_total = C_1 + C_2 + ... + C_n`, where each `C_i = salary_i * G + blinding_i * H`. The employer commits to each salary individually, and the sum is verifiable without opening any individual commitment.
- **BST-VC employment credentials**: Each employee receives a BST-VC credential (type 0x0007) from the employer confirming their employment. This credential is used in the ZK claim proof to demonstrate eligibility without revealing identity to on-chain observers.
- **ZkComplianceVerifier**: The Groth16 proof that verifies "I hold a valid employment credential and my claim amount matches my individual commitment" is verified on-chain using Basalt's native ZK verification infrastructure.
- **IssuerRegistry employer trust**: The employer registers as an issuer in IssuerRegistry (Tier 0 self-attestation or Tier 1 regulated), and their employment credentials inherit the trust level. If the employer is slashed or deactivated, all employment credentials are implicitly invalidated.
- **SchemaRegistry payroll schema**: The payroll credential schema (employeeId, salaryCommitment, payPeriod, jurisdiction) is standardized via SchemaRegistry, enabling interoperability with accounting and tax systems.
- **AOT-compiled claim processing**: Each salary claim is processed by AOT-compiled contract code with deterministic gas costs, ensuring predictable per-employee processing costs.
- **Nullifier-based claim tracking**: Each employee's claim for a specific pay period generates a unique nullifier, preventing double-claiming while preserving privacy (observers cannot correlate claims across pay periods).
- **Cross-contract Escrow**: Employer funds are held in the Escrow contract until the pay period deadline, protecting employees from employer withdrawal before all claims are processed.

## Token Standards Used

- **BST-VC** (BSTVCRegistry, type 0x0007): Employment credentials issued by the employer to each employee. Used in ZK claim proofs to prove employment eligibility.
- **BST-20** (BST20Token, type 0x0001): For payroll denominated in BST-20 stablecoins rather than native BST.

## Integration Points

- **Escrow** (0x...1003): Employer payroll deposits are held in Escrow until the claim period ends. Unclaimed funds are returned to the employer after a configurable grace period.
- **IssuerRegistry** (0x...1007): Employers register as issuers to issue employment credentials. The contract validates employer issuer status before accepting payroll batches.
- **BSTVCRegistry** (deployed instance): Employment credentials are issued and managed through BSTVCRegistry. Credential validity is checked during claim processing.
- **SchemaRegistry** (0x...1006): The payroll credential schema and claim proof verification key are registered in SchemaRegistry.
- **Governance** (0x...1002): System parameters (maximum payroll size, fee rates, grace periods) can be modified through governance proposals.
- **BNS** (Basalt Name Service): Employers can register their BNS name for human-readable payroll identification.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Private payroll system: employer deposits lump sum, employees claim via ZK proof
/// of employment without revealing individual salary amounts.
/// Type ID: 0x010F.
/// </summary>
[BasaltContract]
public partial class PrivatePayroll
{
    // --- Storage ---

    // Employer registration
    private readonly StorageMap<string, bool> _employerRegistered;     // employerHex -> registered
    private readonly StorageMap<string, string> _employerName;        // employerHex -> name

    // Payroll batches
    private readonly StorageValue<ulong> _nextBatchId;
    private readonly StorageMap<string, string> _batchEmployer;       // batchId -> employerHex
    private readonly StorageMap<string, string> _batchTotalCommitment; // batchId -> total Pedersen commitment hex
    private readonly StorageMap<string, UInt256> _batchTotalDeposit;  // batchId -> actual deposited amount
    private readonly StorageMap<string, string> _batchStatus;         // batchId -> funded/active/completed/cancelled
    private readonly StorageMap<string, long> _batchPeriodStart;     // batchId -> pay period start timestamp
    private readonly StorageMap<string, long> _batchPeriodEnd;       // batchId -> pay period end timestamp
    private readonly StorageMap<string, long> _batchClaimDeadline;   // batchId -> claim deadline timestamp
    private readonly StorageMap<string, ulong> _batchEmployeeCount;  // batchId -> number of employees
    private readonly StorageMap<string, ulong> _batchClaimCount;     // batchId -> number of claims processed
    private readonly StorageMap<string, UInt256> _batchClaimedTotal; // batchId -> total claimed amount
    private readonly StorageMap<string, ulong> _batchEscrowId;       // batchId -> Escrow contract escrow ID

    // Employee commitments per batch
    private readonly StorageMap<string, string> _employeeCommitment;  // batchId:index -> commitment hex
    private readonly StorageMap<string, bool> _employeeCommitmentSet; // batchId:index -> set

    // Claim tracking (nullifier-based)
    private readonly StorageMap<string, bool> _claimNullifierUsed;    // batchId:nullifierHex -> claimed

    // Schema reference
    private readonly StorageMap<string, string> _payrollSchemaId;     // "schema" -> schema ID hex

    // Admin
    private readonly StorageMap<string, string> _admin;

    // Protocol configuration
    private readonly StorageValue<ulong> _protocolFeeBps;
    private readonly StorageValue<UInt256> _protocolFeeBalance;

    // System contract addresses
    private readonly byte[] _escrowAddress;
    private readonly byte[] _issuerRegistryAddress;
    private readonly byte[] _schemaRegistryAddress;

    public PrivatePayroll(ulong protocolFeeBps = 25)
    {
        _employerRegistered = new StorageMap<string, bool>("pp_empreg");
        _employerName = new StorageMap<string, string>("pp_empname");

        _nextBatchId = new StorageValue<ulong>("pp_nextbatch");
        _batchEmployer = new StorageMap<string, string>("pp_bemp");
        _batchTotalCommitment = new StorageMap<string, string>("pp_btotalc");
        _batchTotalDeposit = new StorageMap<string, UInt256>("pp_btotald");
        _batchStatus = new StorageMap<string, string>("pp_bstatus");
        _batchPeriodStart = new StorageMap<string, long>("pp_bstart");
        _batchPeriodEnd = new StorageMap<string, long>("pp_bend");
        _batchClaimDeadline = new StorageMap<string, long>("pp_bdeadline");
        _batchEmployeeCount = new StorageMap<string, ulong>("pp_bempcount");
        _batchClaimCount = new StorageMap<string, ulong>("pp_bclaimcount");
        _batchClaimedTotal = new StorageMap<string, UInt256>("pp_bclaimed");
        _batchEscrowId = new StorageMap<string, ulong>("pp_bescrow");

        _employeeCommitment = new StorageMap<string, string>("pp_ecommit");
        _employeeCommitmentSet = new StorageMap<string, bool>("pp_ecommitset");

        _claimNullifierUsed = new StorageMap<string, bool>("pp_cnull");

        _payrollSchemaId = new StorageMap<string, string>("pp_schema");

        _admin = new StorageMap<string, string>("pp_admin");
        _admin.Set("admin", Convert.ToHexString(Context.Caller));

        _protocolFeeBps = new StorageValue<ulong>("pp_pfee");
        _protocolFeeBalance = new StorageValue<UInt256>("pp_pfeebal");
        _protocolFeeBps.Set(protocolFeeBps);

        _escrowAddress = new byte[20];
        _escrowAddress[18] = 0x10;
        _escrowAddress[19] = 0x03;

        _issuerRegistryAddress = new byte[20];
        _issuerRegistryAddress[18] = 0x10;
        _issuerRegistryAddress[19] = 0x07;

        _schemaRegistryAddress = new byte[20];
        _schemaRegistryAddress[18] = 0x10;
        _schemaRegistryAddress[19] = 0x06;
    }

    // ========================================================
    // Employer Registration
    // ========================================================

    /// <summary>
    /// Register as an employer. Must be an active issuer in IssuerRegistry.
    /// </summary>
    [BasaltEntrypoint]
    public void RegisterEmployer(string name)
    {
        Context.Require(!string.IsNullOrEmpty(name), "PP: name required");
        var employerHex = Convert.ToHexString(Context.Caller);

        // Verify issuer status
        var isActive = Context.CallContract<bool>(
            _issuerRegistryAddress, "IsActiveIssuer", Context.Caller);
        Context.Require(isActive, "PP: must be active issuer");

        _employerRegistered.Set(employerHex, true);
        _employerName.Set(employerHex, name);

        Context.Emit(new EmployerRegisteredEvent
        {
            Employer = Context.Caller,
            Name = name,
        });
    }

    // ========================================================
    // Payroll Batch Management
    // ========================================================

    /// <summary>
    /// Create a new payroll batch. Employer deposits the total payroll amount.
    /// The total commitment is a Pedersen commitment to the sum of all salaries.
    /// </summary>
    [BasaltEntrypoint]
    public ulong CreatePayrollBatch(
        byte[] totalCommitment, ulong employeeCount,
        long periodStart, long periodEnd, long claimDeadline)
    {
        var employerHex = Convert.ToHexString(Context.Caller);
        Context.Require(_employerRegistered.Get(employerHex), "PP: not registered employer");
        Context.Require(!Context.TxValue.IsZero, "PP: must deposit payroll funds");
        Context.Require(totalCommitment.Length == 32, "PP: invalid commitment");
        Context.Require(employeeCount > 0, "PP: must have employees");
        Context.Require(periodEnd > periodStart, "PP: invalid period");
        Context.Require(claimDeadline > periodEnd, "PP: deadline must be after period end");

        var batchId = _nextBatchId.Get();
        _nextBatchId.Set(batchId + 1);

        var key = batchId.ToString();

        // Deposit into escrow
        var releaseBlock = Context.BlockHeight + 200000; // generous timeout
        var escrowId = Context.CallContract<ulong>(
            _escrowAddress, "Create", Context.Caller, releaseBlock);

        _batchEmployer.Set(key, employerHex);
        _batchTotalCommitment.Set(key, Convert.ToHexString(totalCommitment));
        _batchTotalDeposit.Set(key, Context.TxValue);
        _batchStatus.Set(key, "funded");
        _batchPeriodStart.Set(key, periodStart);
        _batchPeriodEnd.Set(key, periodEnd);
        _batchClaimDeadline.Set(key, claimDeadline);
        _batchEmployeeCount.Set(key, employeeCount);
        _batchClaimCount.Set(key, 0);
        _batchClaimedTotal.Set(key, UInt256.Zero);
        _batchEscrowId.Set(key, escrowId);

        Context.Emit(new PayrollBatchCreatedEvent
        {
            BatchId = batchId,
            Employer = Context.Caller,
            EmployeeCount = employeeCount,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
        });

        return batchId;
    }

    /// <summary>
    /// Register an employee's salary commitment for a batch. Employer only.
    /// Each commitment is C_i = salary_i * G + blinding_i * H.
    /// </summary>
    [BasaltEntrypoint]
    public void RegisterEmployeeCommitment(ulong batchId, ulong employeeIndex, byte[] commitment)
    {
        var key = batchId.ToString();
        RequireBatchEmployer(key);
        Context.Require(_batchStatus.Get(key) == "funded", "PP: batch not in funded state");
        Context.Require(employeeIndex < _batchEmployeeCount.Get(key), "PP: invalid employee index");
        Context.Require(commitment.Length == 32, "PP: invalid commitment");

        var ek = key + ":" + employeeIndex;
        Context.Require(!_employeeCommitmentSet.Get(ek), "PP: commitment already set");

        _employeeCommitment.Set(ek, Convert.ToHexString(commitment));
        _employeeCommitmentSet.Set(ek, true);
    }

    /// <summary>
    /// Activate the batch after all employee commitments are registered.
    /// Verifies that commitments sum to total commitment (homomorphic check).
    /// Employer only.
    /// </summary>
    [BasaltEntrypoint]
    public void ActivateBatch(ulong batchId, byte[] sumProof)
    {
        var key = batchId.ToString();
        RequireBatchEmployer(key);
        Context.Require(_batchStatus.Get(key) == "funded", "PP: batch not funded");
        Context.Require(sumProof.Length > 0, "PP: sum proof required");

        // Verify all commitments are registered
        var empCount = _batchEmployeeCount.Get(key);
        for (ulong i = 0; i < empCount; i++)
        {
            Context.Require(
                _employeeCommitmentSet.Get(key + ":" + i),
                "PP: missing employee commitment");
        }

        // In production, verify sumProof shows:
        // sum(employee_commitments) == total_commitment
        // This uses the homomorphic property of Pedersen commitments.

        _batchStatus.Set(key, "active");

        Context.Emit(new PayrollBatchActivatedEvent { BatchId = batchId });
    }

    // ========================================================
    // Employee Claims
    // ========================================================

    /// <summary>
    /// Employee claims their salary for a batch.
    /// Provides a ZK proof demonstrating:
    ///   1. They hold a valid BST-VC employment credential from this employer
    ///   2. Their claim amount matches their individual Pedersen commitment
    ///   3. The nullifier is correctly derived (prevents double-claim)
    ///
    /// The actual salary amount is transferred but hidden from on-chain observers
    /// (only commitment is public, not the opening).
    /// </summary>
    [BasaltEntrypoint]
    public void ClaimSalary(
        ulong batchId, byte[] proofData, byte[] nullifier,
        UInt256 claimAmount, byte[] blindingFactor)
    {
        var key = batchId.ToString();
        Context.Require(_batchStatus.Get(key) == "active", "PP: batch not active");
        Context.Require(Context.BlockTimestamp <= _batchClaimDeadline.Get(key), "PP: claim deadline passed");
        Context.Require(proofData.Length > 0, "PP: proof required");
        Context.Require(nullifier.Length == 32, "PP: invalid nullifier");

        // Check nullifier (prevent double-claim)
        var nullifierHex = Convert.ToHexString(nullifier);
        var nullKey = key + ":" + nullifierHex;
        Context.Require(!_claimNullifierUsed.Get(nullKey), "PP: already claimed");

        // In production: verify ZK proof against schema verification key
        // The proof verifies employment credential + commitment opening

        // Mark nullifier as used
        _claimNullifierUsed.Set(nullKey, true);

        // Calculate protocol fee
        var feeBps = _protocolFeeBps.Get();
        var fee = claimAmount * new UInt256(feeBps) / new UInt256(10000);
        var netAmount = claimAmount - fee;

        // Track cumulative claims
        var claimed = _batchClaimedTotal.Get(key);
        _batchClaimedTotal.Set(key, claimed + claimAmount);

        var claimCount = _batchClaimCount.Get(key);
        _batchClaimCount.Set(key, claimCount + 1);

        // Track protocol fees
        var totalFees = _protocolFeeBalance.Get();
        _protocolFeeBalance.Set(totalFees + fee);

        // Transfer salary to employee
        Context.TransferNative(Context.Caller, netAmount);

        Context.Emit(new SalaryClaimedEvent
        {
            BatchId = batchId,
            Nullifier = nullifier,
            ClaimNumber = claimCount + 1,
        });

        // Check if batch is complete
        if (claimCount + 1 == _batchEmployeeCount.Get(key))
        {
            _batchStatus.Set(key, "completed");
            Context.Emit(new PayrollBatchCompletedEvent { BatchId = batchId });
        }
    }

    /// <summary>
    /// Employer reclaims unclaimed funds after the claim deadline.
    /// Only available if not all employees have claimed.
    /// </summary>
    [BasaltEntrypoint]
    public void ReclaimUnclaimed(ulong batchId)
    {
        var key = batchId.ToString();
        RequireBatchEmployer(key);
        Context.Require(_batchStatus.Get(key) == "active", "PP: batch not active");
        Context.Require(Context.BlockTimestamp > _batchClaimDeadline.Get(key), "PP: deadline not passed");

        var totalDeposit = _batchTotalDeposit.Get(key);
        var claimed = _batchClaimedTotal.Get(key);
        var unclaimed = totalDeposit - claimed;

        Context.Require(!unclaimed.IsZero, "PP: nothing to reclaim");

        _batchStatus.Set(key, "completed");

        // Return unclaimed funds to employer
        var employerHex = _batchEmployer.Get(key);
        Context.TransferNative(Convert.FromHexString(employerHex), unclaimed);

        Context.Emit(new UnclaimedReclaimedEvent
        {
            BatchId = batchId,
            Amount = unclaimed,
        });
    }

    // ========================================================
    // Admin
    // ========================================================

    /// <summary>
    /// Set the payroll credential schema ID. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void SetPayrollSchema(byte[] schemaId)
    {
        RequireAdmin();
        _payrollSchemaId.Set("schema", Convert.ToHexString(schemaId));
    }

    /// <summary>
    /// Update protocol fee. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void SetProtocolFee(ulong feeBps)
    {
        RequireAdmin();
        Context.Require(feeBps <= 500, "PP: fee too high");
        _protocolFeeBps.Set(feeBps);
    }

    /// <summary>
    /// Withdraw accumulated protocol fees. Admin only.
    /// </summary>
    [BasaltEntrypoint]
    public void WithdrawProtocolFees(byte[] destination)
    {
        RequireAdmin();
        var fees = _protocolFeeBalance.Get();
        Context.Require(!fees.IsZero, "PP: no fees");
        _protocolFeeBalance.Set(UInt256.Zero);
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
    public string GetBatchStatus(ulong batchId)
        => _batchStatus.Get(batchId.ToString()) ?? "unknown";

    [BasaltView]
    public ulong GetBatchEmployeeCount(ulong batchId)
        => _batchEmployeeCount.Get(batchId.ToString());

    [BasaltView]
    public ulong GetBatchClaimCount(ulong batchId)
        => _batchClaimCount.Get(batchId.ToString());

    [BasaltView]
    public long GetBatchClaimDeadline(ulong batchId)
        => _batchClaimDeadline.Get(batchId.ToString());

    [BasaltView]
    public bool IsEmployerRegistered(byte[] employer)
        => _employerRegistered.Get(Convert.ToHexString(employer));

    [BasaltView]
    public bool IsNullifierUsed(ulong batchId, byte[] nullifier)
        => _claimNullifierUsed.Get(batchId.ToString() + ":" + Convert.ToHexString(nullifier));

    [BasaltView]
    public ulong GetProtocolFeeBps() => _protocolFeeBps.Get();

    // ========================================================
    // Internal Helpers
    // ========================================================

    private void RequireBatchEmployer(string batchKey)
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _batchEmployer.Get(batchKey),
            "PP: not batch employer");
    }

    private void RequireAdmin()
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _admin.Get("admin"),
            "PP: not admin");
    }
}

// ========================================================
// Events
// ========================================================

[BasaltEvent]
public class EmployerRegisteredEvent
{
    [Indexed] public byte[] Employer { get; set; } = null!;
    public string Name { get; set; } = "";
}

[BasaltEvent]
public class PayrollBatchCreatedEvent
{
    [Indexed] public ulong BatchId { get; set; }
    [Indexed] public byte[] Employer { get; set; } = null!;
    public ulong EmployeeCount { get; set; }
    public long PeriodStart { get; set; }
    public long PeriodEnd { get; set; }
}

[BasaltEvent]
public class PayrollBatchActivatedEvent
{
    [Indexed] public ulong BatchId { get; set; }
}

[BasaltEvent]
public class SalaryClaimedEvent
{
    [Indexed] public ulong BatchId { get; set; }
    public byte[] Nullifier { get; set; } = null!;
    public ulong ClaimNumber { get; set; }
}

[BasaltEvent]
public class PayrollBatchCompletedEvent
{
    [Indexed] public ulong BatchId { get; set; }
}

[BasaltEvent]
public class UnclaimedReclaimedEvent
{
    [Indexed] public ulong BatchId { get; set; }
    public UInt256 Amount { get; set; }
}
```

## Complexity

**High** -- The contract combines Pedersen commitment arithmetic (homomorphic sum verification), ZK proof verification for employment credentials and commitment openings, nullifier-based double-claim prevention, multi-phase batch lifecycle management, and escrow coordination. The interaction between committed amounts, actual transfer amounts, and fee calculations requires careful cryptographic design. The off-chain circuit for proving employment credential validity combined with commitment opening is non-trivial.

## Priority

**P2** -- Private payroll is a compelling enterprise use case that demonstrates Basalt's privacy capabilities in a business context. However, it requires Pedersen commitment infrastructure, ZK circuits, employment credential issuance via BST-VC, and the IssuerRegistry pipeline to be fully operational. It is best prioritized after the core privacy (compliant privacy pool) and compliance (KYC marketplace) infrastructure is proven.
