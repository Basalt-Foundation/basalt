# Basalt.Consensus

BFT consensus engine for the Basalt blockchain. Implements a HotStuff-inspired three-phase protocol with pipelined block production, proof-of-stake validator management, and slashing for misbehavior.

## Components

### BasaltBft

Core consensus state machine with PREPARE -> PRE-COMMIT -> COMMIT phases. Votes are signed with BLS12-381 and tracked for aggregation.

```csharp
var bft = new BasaltBft(validatorSet, localPeerId, privateKey, logger, blsSigner: null);
// 5th parameter is optional IBlsSigner? (defaults to new BlsSigner())

bft.OnBlockFinalized += (hash, data) => { /* commit block */ };
bft.OnViewChange += (newView) => { /* leader rotation */ };

// Leader proposes
bft.StartRound(blockNumber);
ConsensusProposalMessage? proposal = bft.ProposeBlock(blockData, blockHash);

// Validators vote (nullable returns -- null means no phase transition)
ConsensusVoteMessage? vote = bft.HandleProposal(proposal);
ConsensusVoteMessage? nextVote = bft.HandleVote(incomingVote);

// View change on timeout (2 second default)
ViewChangeMessage? viewChange = bft.CheckViewTimeout();
bft.HandleViewChange(viewChangeMsg);  // Process view change from peers

// Properties
ConsensusState state = bft.State;           // Idle, Proposing, Preparing, PreCommitting, Committing, Finalized
ulong view = bft.CurrentView;
ulong blockNum = bft.CurrentBlockNumber;
bool isLeader = bft.IsLeader;
```

Quorum threshold: 2f+1 out of 3f+1 validators (computed as `(count * 2 / 3) + 1`).

Self-vote behavior: when the leader calls `ProposeBlock`, it immediately counts its own PREPARE vote locally before returning the proposal for broadcast. When a validator calls `HandleProposal`, it likewise self-counts its PREPARE vote before returning the vote message. At each phase transition (PREPARE quorum reached, PRE-COMMIT quorum reached), the node self-counts the next phase vote before broadcasting it. This ensures correct quorum counting without waiting for its own vote to round-trip through the network.

View change votes use key `(VotePhase)0xFF` to avoid collision with consensus PREPARE/PRE-COMMIT/COMMIT vote tracking. On 2f+1 view change votes, the view advances and all vote state is cleared.

### PipelinedConsensus

Overlaps consensus phases across consecutive blocks for higher throughput. Up to 3 blocks can be in different consensus stages simultaneously.

```csharp
var pipeline = new PipelinedConsensus(
    validatorSet, localPeerId, privateKey, blsSigner, logger,
    lastFinalizedBlock: 0);  // IBlsSigner is required, lastFinalizedBlock is optional

pipeline.OnBlockFinalized += (hash, data) => { /* commit block */ };
pipeline.OnViewChange += (newView) => { /* leader rotation */ };

ConsensusProposalMessage? proposal = pipeline.StartRound(blockNumber, blockData, blockHash);
ConsensusVoteMessage? vote = pipeline.HandleProposal(proposalMsg);
ConsensusVoteMessage? nextVote = pipeline.HandleVote(incomingVote);
ViewChangeMessage? viewChange = pipeline.CheckViewTimeout();
pipeline.HandleViewChange(viewChangeMsg);

int active = pipeline.ActiveRoundCount;          // Up to 3
ulong lastFinalized = pipeline.LastFinalizedBlock;
ConsensusState? roundState = pipeline.GetRoundState(blockNumber);
byte[]? aggSig = pipeline.GetAggregateSignature(blockNumber); // BLS aggregate for finalized round
pipeline.CleanupFinalizedRounds();
```

Enforces sequential finalization ordering: block N must finalize before N+1 is released. If block N+1 reaches COMMIT quorum before block N, it is buffered and drained once N finalizes.

Self-vote cascade: in single-validator or low-quorum scenarios, after recording a self-vote the engine cascades through PREPARE -> PRE-COMMIT -> COMMIT -> FINALIZED in a single call if quorum is already met at each step.

View change in pipelined mode aborts all non-finalized in-flight rounds.

### ValidatorSet

Manages the active validator set with quorum calculations.

```csharp
var validators = new ValidatorSet(validatorInfos);
int quorum = validators.QuorumThreshold;       // 2f+1
int maxFaults = validators.MaxFaults;          // f = (n-1)/3
ValidatorInfo leader = validators.GetLeader(viewNumber);
bool isValidator = validators.IsValidator(peerId);

IReadOnlyList<ValidatorInfo> all = validators.Validators;
ValidatorInfo? byPeer = validators.GetByPeerId(peerId);
ValidatorInfo? byAddr = validators.GetByAddress(address);
int count = validators.Count;

// Custom leader selection (e.g., stake-weighted via WeightedLeaderSelector)
validators.SetLeaderSelector(viewNumber => selectLeaderByStake(viewNumber));

// Transfer identities from a previous set (used during epoch transitions)
newSet.TransferIdentities(previousSet);
```

Default leader selection is equal-weight round-robin: `viewNumber % validatorCount`.

`TransferIdentities` copies `PeerId`, `PublicKey`, and `BlsPublicKey` from a previous `ValidatorSet` for validators that appear in both sets (matched by `Address`). This preserves network identity across epoch transitions so that established P2P connections remain valid.

Both `BasaltBft` and `PipelinedConsensus` expose `UpdateValidatorSet(ValidatorSet newSet)` for atomic validator set replacement during epoch transitions. This clears all in-flight vote state and resets consensus to `Idle`.

### ValidatorInfo

Full validator identity and stake information.

```csharp
public sealed class ValidatorInfo
{
    public required PeerId PeerId { get; init; }
    public required PublicKey PublicKey { get; init; }
    public required BlsPublicKey BlsPublicKey { get; init; }
    public required Address Address { get; init; }
    public UInt256 Stake { get; set; }   // Default: UInt256.Zero
    public int Index { get; init; }
}
```

### WeightedLeaderSelector

Selects block proposers based on stake weight using deterministic pseudo-randomness.

```csharp
var selector = new WeightedLeaderSelector(validatorSet, stakingState);
ValidatorInfo leader = selector.SelectLeader(viewNumber);
```

Selection algorithm: computes a deterministic seed via `BLAKE3(viewNumber)`, takes the first 8 bytes as a `ulong`, then selects a validator using cumulative stake-weighted random selection. The weight for each validator is derived from the low 8 bytes of their `TotalStake` (minimum weight of 1). Falls back to round-robin if total weight is zero.

### StakingState

In-memory staking state for validator registration, delegation, and unbonding. Located in `Basalt.Consensus.Staking` namespace.

```csharp
var staking = new StakingState { MinValidatorStake = minStake, UnbondingPeriod = 4_536_000 };

StakingResult result = staking.RegisterValidator(address, initialStake);
StakingResult result = staking.RegisterValidator(address, initialStake, blockNumber, p2pEndpoint);
StakingResult result = staking.AddStake(address, additionalAmount);
StakingResult result = staking.Delegate(delegatorAddr, validatorAddr, amount);
StakingResult result = staking.InitiateUnstake(address, amount, currentBlock);

List<UnbondingEntry> completed = staking.ProcessUnbonding(currentBlock);
List<StakeInfo> active = staking.GetActiveValidators();
StakeInfo? info = staking.GetStakeInfo(validatorAddr);
UInt256? selfStake = staking.GetSelfStake(validatorAddr);
UInt256 total = staking.TotalStaked;  // Sum of all validator stakes
```

`StakingState` also implements `IStakingState` (defined in `Basalt.Core`) via explicit interface implementation, allowing the execution layer to interact with staking without a direct dependency on the consensus assembly.

Default `MinValidatorStake` is `100000000000000000000000` (100,000 tokens at 10^18 precision). Default `UnbondingPeriod` is 4,536,000 blocks (~21 days at 400ms blocks).

`InitiateUnstake` enforces that remaining stake must either be zero or at least `MinValidatorStake`. When self-stake drops to zero, the validator is deactivated.

**StakingResult**: `readonly struct` with `IsSuccess`, `ErrorMessage`. Factory: `StakingResult.Ok()`, `StakingResult.Error(message)`.

**StakeInfo**: Full validator stake information:

```csharp
public sealed class StakeInfo
{
    public required Address Address { get; init; }
    public UInt256 SelfStake { get; set; }
    public UInt256 DelegatedStake { get; set; }
    public UInt256 TotalStake { get; set; }
    public bool IsActive { get; set; }
    public ulong RegisteredAtBlock { get; set; }
    public string P2PEndpoint { get; set; }   // Optional, set during ValidatorRegister tx
    public Dictionary<Address, UInt256> Delegators { get; }
}
```

**UnbondingEntry**: `Validator` (Address), `Amount` (UInt256), `UnbondingCompleteBlock` (ulong).

### SlashingEngine

Penalizes validators for provable misbehavior. Located in `Basalt.Consensus.Staking` namespace.

| Offense | Penalty | Constant |
|---------|---------|----------|
| Double signing | 100% of stake | `DoubleSignPenaltyPercent` |
| Inactivity | 5% of stake | `InactivityPenaltyPercent` |
| Invalid block | 1% of stake | `InvalidBlockPenaltyPercent` |

```csharp
var slashing = new SlashingEngine(stakingState, logger);

SlashingResult result = slashing.SlashDoubleSign(validator, blockNumber, hash1, hash2);
SlashingResult result = slashing.SlashInactivity(validator, fromBlock, toBlock);
SlashingResult result = slashing.SlashInvalidBlock(validator, blockNumber, "invalid state root");

IReadOnlyList<SlashingEvent> history = slashing.SlashingHistory;

if (result.IsSuccess)
    UInt256 penalized = result.PenaltyApplied;
```

Slash application: penalty is deducted from self-stake first; any remainder is taken from delegated stake. If remaining total stake falls below `MinValidatorStake`, the validator is deactivated.

**SlashingResult**: `readonly struct` with `IsSuccess`, `PenaltyApplied` (UInt256), `ErrorMessage`. Factory: `SlashingResult.Success(penalty)`, `SlashingResult.Error(message)`.

**SlashingEvent**: `Validator` (Address), `Reason` (SlashingReason), `Penalty` (UInt256), `BlockNumber` (ulong), `Description` (string), `Timestamp` (DateTimeOffset).

**SlashingReason enum**: `DoubleSign`, `Inactivity`, `InvalidBlock`.

### EpochManager

Detects epoch boundaries and rebuilds the `ValidatorSet` from `StakingState`. An epoch transition occurs every `ChainParameters.EpochLength` blocks.

```csharp
var epochManager = new EpochManager(chainParams, stakingState, initialValidatorSet, blsSigner);

epochManager.OnEpochTransition += (epoch, newSet) => { /* apply new validator set */ };

// Call after each block is finalized
ValidatorSet? newSet = epochManager.OnBlockFinalized(blockNumber);
if (newSet != null)
{
    // Epoch transition occurred — swap validator set in consensus engine
    consensus.UpdateValidatorSet(newSet);
}

// Utility methods
ulong epoch = EpochManager.ComputeEpoch(blockNumber, epochLength);
bool isBoundary = epochManager.IsEpochBoundary(blockNumber);
ValidatorSet built = epochManager.BuildValidatorSetFromStaking();

// Properties
ulong currentEpoch = epochManager.CurrentEpoch;
ValidatorSet currentSet = epochManager.CurrentSet;
```

Rebuild algorithm:
1. Query `StakingState.GetActiveValidators()` (sorted by `TotalStake` descending)
2. Cap at `ChainParameters.ValidatorSetSize` (top N by stake)
3. Sort selected validators by `Address` ascending (deterministic across all nodes)
4. Assign sequential indices 0..N-1
5. Call `TransferIdentities(previousSet)` to preserve network identity

**ConsensusState enum**: `Idle`, `Proposing`, `Preparing`, `PreCommitting`, `Committing`, `Finalized`.

## Dependencies

| Package | Purpose |
|---------|---------|
| `Basalt.Core` | Hash256, Address, UInt256, PublicKey, BlsPublicKey, BlsSignature |
| `Basalt.Network` | PeerId, PeerInfo, ValidatorSet uses PeerId; consensus message types |
| `Basalt.Crypto` | IBlsSigner, BlsSigner (BLS12-381), BLAKE3 for WeightedLeaderSelector seed |
| `Microsoft.Extensions.Logging.Abstractions` | Structured logging |
