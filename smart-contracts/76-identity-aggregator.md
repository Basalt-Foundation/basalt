# Cross-Chain Identity Aggregator

## Category

Identity and Compliance -- Cross-Chain Reputation Infrastructure

## Summary

A cross-chain identity aggregation protocol that collects and unifies identity credentials, reputation signals, and asset ownership proofs from multiple blockchains and identity providers into a single Basalt-native profile. Users can prove Ethereum NFT ownership, Solana DeFi history, Bitcoin holdings, and centralized exchange verification status through bridge attestations, receiving BST-VC credentials on Basalt that represent their cross-chain identity. This enables DeFi protocols on Basalt to leverage the full breadth of a user's blockchain history.

## Why It's Useful

- **Portable Reputation**: Users should not have to rebuild their reputation from scratch on every new chain. Cross-chain identity aggregation lets established users bring their track record to Basalt immediately.
- **Better Risk Assessment**: Lending protocols, credit scoring systems, and insurance contracts can make more informed decisions when they have access to a user's full cross-chain history rather than just their Basalt activity.
- **Ecosystem Onboarding**: Lowering the barrier for experienced DeFi users from other chains to participate on Basalt by recognizing their existing credentials and history.
- **Unified KYC**: Users who have completed KYC on one chain or centralized exchange can reuse that verification on Basalt without repeating the process, via credential bridging.
- **Sybil Resistance**: Cross-chain identity links make it harder to create fresh sybil accounts, as genuine users can prove long histories across chains.
- **Airdrop and Governance Qualification**: Projects launching on Basalt can use cross-chain identity to qualify users for airdrops, governance participation, or early access based on their activity on other chains.
- **DID Interoperability**: W3C DID (Decentralized Identifier) compatibility enables integration with off-chain identity providers and the broader verifiable credential ecosystem.

## Key Features

- **Multi-Chain Attestation**: Accept attestations from multiple chains (Ethereum, Solana, Bitcoin, Cosmos, Polkadot) via the generic bridge or dedicated attestation relayers.
- **Attestation Types**:
  - **Asset Ownership**: Prove ownership of specific tokens, NFTs, or positions on external chains.
  - **Activity History**: Prove transaction count, account age, protocol usage, and DeFi participation on external chains.
  - **KYC Status**: Bridge KYC verification status from centralized exchanges or other chains' identity systems.
  - **Governance Participation**: Prove voting history, delegation, and governance participation on external DAOs.
  - **Staking History**: Prove validator or delegator status on other proof-of-stake networks.
- **BST-VC Credential Issuance**: Each verified cross-chain attestation results in a BST-VC credential issued on Basalt, compatible with the SchemaRegistry and IssuerRegistry.
- **Attestation Verification**: Attestations are verified via M-of-N relayer signatures (same pattern as GenericBridge), Merkle proofs against external chain state roots, or ZK proofs of state inclusion.
- **Profile Aggregation**: All credentials linked to a single Basalt address, forming a unified identity profile queryable by any protocol.
- **ZK Identity Proofs**: Users generate ZK proofs about their aggregated identity (e.g., "I have over 100 ETH in historical DeFi volume across all chains") without revealing specific chain addresses or transaction details.
- **Credential Revocation**: Attestation sources can revoke credentials if the underlying claim becomes invalid (e.g., user sells the NFT they proved ownership of).
- **Time-Bounded Credentials**: Credentials have configurable expiry periods, requiring periodic re-attestation to remain valid.
- **Privacy-Preserving Linking**: Users link cross-chain addresses without publicly revealing the link. The connection is known only to the aggregator contract and provable via ZK proofs.
- **Credential Composability**: Compose multiple credentials into aggregate claims (e.g., "top 10% of DeFi users across all chains") using set membership proofs.
- **Reputation Score**: Compute a unified reputation score from aggregated cross-chain credentials, weighted by chain and activity type.
- **Selective Disclosure**: Users choose which credentials to reveal to which protocols, maintaining minimal disclosure.

## Basalt-Specific Advantages

- **BST-VC Native Integration**: Basalt's W3C-compatible BST-VC standard is the ideal format for cross-chain credentials. Each attestation becomes a standardized verifiable credential natively understood by every Basalt protocol, unlike EVM chains where custom credential formats require bespoke integration.
- **SchemaRegistry and IssuerRegistry**: Cross-chain attestation schemas are registered in the SchemaRegistry, and attestation sources (bridge relayers, oracle providers) are registered in the IssuerRegistry. This provides a standardized trust framework for cross-chain identity that does not exist on other chains.
- **ZK Compliance Layer for Private Identity**: Basalt's Groth16 verifier and nullifier system enable users to prove claims about their aggregated identity without revealing which addresses they own on other chains. This is critical for privacy -- linking all your cross-chain addresses publicly would expose your entire financial history.
- **Nullifier Anti-Correlation**: When proving cross-chain identity claims to different protocols, Basalt's nullifier system ensures the proofs cannot be correlated to determine that the same user is interacting with multiple protocols.
- **Confidential Attestation Amounts**: Pedersen commitments can hide the specific amounts in cross-chain attestations (e.g., proving "staked more than 32 ETH" without revealing the exact amount) via range proofs.
- **AOT-Compiled Merkle Verification**: Verifying Merkle proofs against external chain state roots (the core attestation verification mechanism) executes in AOT-compiled native code, reducing verification costs for cross-chain proofs.
- **Ed25519 Relayer Efficiency**: Attestation relayers use Ed25519 signatures (Basalt's native signature scheme), providing faster verification than ECDSA or BLS for individual attestations.
- **BLS Aggregate Attestations**: Batch attestations from multiple relayers can be aggregated into a single BLS signature, enabling efficient mass-verification of cross-chain identity data.

## Token Standards Used

- **BST-VC (Verifiable Credentials)**: The primary output of the aggregator. Each verified cross-chain attestation becomes a BST-VC credential with standardized schema, issuer, and expiry.
- **BST-721**: Soulbound identity profile token. Each aggregated identity is represented as a non-transferable BST-721 token with metadata pointing to all linked credentials.
- **BST-3525 (SFT)**: Reputation score positions with slot metadata for score breakdown by chain, activity type, and credential category.
- **BST-20**: Attestation fees and relayer compensation.

## Integration Points

- **SchemaRegistry (0x...1006)**: Defines schemas for all cross-chain credential types: "EthereumNftOwnershipV1", "SolanaDefiHistoryV1", "CentralizedExchangeKycV1", "GovernanceParticipationV1", etc.
- **IssuerRegistry (0x...1007)**: Registers attestation sources (bridge relayers, oracle providers, exchange attestation services) as trusted credential issuers.
- **BridgeETH (0x...1008)**: Ethereum-specific attestations can use the existing BridgeETH for Merkle proof verification against Ethereum state roots.
- **Governance (0x0102)**: Governs trusted attestation source registration, schema approval, reputation score model parameters, and credential revocation policy.
- **BNS (0x0101)**: Identity profiles registered under BNS names (e.g., `alice.identity.basalt`).
- **Escrow (0x0103)**: Attestation fees and dispute bonds held in escrow during verification periods.
- **StakingPool (0x0105)**: Staking history attestations verified against StakingPool data. Relayer staking for attestation service quality.

## Technical Sketch

```csharp
// ============================================================
// IdentityAggregator -- Cross-chain identity unification
// ============================================================

[BasaltContract(TypeId = 0x030C)]
public partial class IdentityAggregator : SdkContract
{
    // --- Storage ---

    // Identity profiles
    private StorageMap<Address, IdentityProfile> _profiles;
    private StorageMap<Address, bool> _hasProfile;
    private StorageValue<ulong> _profileCount;

    // Profile token IDs (soulbound BST-721)
    private StorageMap<Address, ulong> _profileTokenIds;
    private StorageValue<ulong> _nextTokenId;

    // Credentials: profileOwner => credentialIndex => Credential
    private StorageMap<Address, StorageMap<ulong, Credential>> _credentials;
    private StorageMap<Address, ulong> _credentialCount;

    // Credential hashes for deduplication
    private StorageMap<Hash256, bool> _issuedCredentials;

    // Attestation sources: sourceId => AttestationSource
    private StorageMap<uint, AttestationSource> _attestationSources;
    private StorageValue<uint> _sourceCount;

    // Chain configurations
    private StorageMap<uint, ChainIdentityConfig> _chainConfigs;

    // Reputation scores: address => score
    private StorageMap<Address, ReputationScore> _reputationScores;

    // Credential schemas per chain activity type
    private StorageMap<Hash256, bool> _approvedSchemas;

    // Relayer registry for attestation verification
    private StorageMap<Address, bool> _trustedRelayers;

    // Attestation fees
    private StorageValue<UInt256> _attestationFee;
    private StorageValue<Address> _feeRecipient;

    // --- Data Structures ---

    public struct IdentityProfile
    {
        public Address Owner;
        public ulong CreatedAtBlock;
        public ulong CredentialCount;
        public uint LinkedChainCount;
        public ushort ReputationScore;     // 0-1000
        public ulong LastUpdatedBlock;
    }

    public struct Credential
    {
        public ulong CredentialIndex;
        public uint SourceChainId;         // 0 = Basalt native, 1 = Ethereum, etc.
        public byte CredentialType;        // 0=asset_ownership, 1=activity_history,
                                           // 2=kyc_status, 3=governance, 4=staking
        public byte[] SchemaHash;          // Reference to SchemaRegistry
        public byte[] ClaimData;           // Encoded claim details
        public Hash256 AttestationHash;    // Hash of the attestation data
        public ulong IssuedAtBlock;
        public ulong ExpiryBlock;
        public bool Revoked;
        public Address Issuer;             // Attestation source address
    }

    public struct AttestationSource
    {
        public uint SourceId;
        public string Name;
        public uint ChainId;
        public Address SourceAddress;
        public byte[] PublicKey;           // Ed25519 public key for verification
        public bool Active;
        public ulong TotalAttestations;
    }

    public struct ChainIdentityConfig
    {
        public uint ChainId;
        public string ChainName;
        public byte VerificationType;     // 0=relayer_attestation, 1=merkle_proof, 2=zk_proof
        public uint RequiredConfirmations;
        public ulong CredentialValidityBlocks; // How long before re-attestation needed
    }

    public struct ReputationScore
    {
        public ushort TotalScore;          // 0-1000
        public ushort AssetScore;          // Component: asset ownership
        public ushort ActivityScore;       // Component: DeFi activity
        public ushort GovernanceScore;     // Component: governance participation
        public ushort StakingScore;        // Component: staking history
        public ushort IdentityScore;       // Component: KYC/identity verification
        public ulong LastComputedBlock;
    }

    // --- Profile Management ---

    /// <summary>
    /// Create a new identity profile. Mints a soulbound BST-721 token.
    /// </summary>
    public ulong CreateProfile()
    {
        Require(!_hasProfile.Get(Context.Sender), "PROFILE_EXISTS");

        _profiles.Set(Context.Sender, new IdentityProfile
        {
            Owner = Context.Sender,
            CreatedAtBlock = Context.BlockNumber,
            CredentialCount = 0,
            LinkedChainCount = 0,
            ReputationScore = 0,
            LastUpdatedBlock = Context.BlockNumber
        });
        _hasProfile.Set(Context.Sender, true);
        _profileCount.Set(_profileCount.Get() + 1);

        // Mint soulbound token
        ulong tokenId = _nextTokenId.Get();
        _nextTokenId.Set(tokenId + 1);
        _profileTokenIds.Set(Context.Sender, tokenId);

        EmitEvent("ProfileCreated", Context.Sender, tokenId);
        return tokenId;
    }

    // --- Cross-Chain Attestation ---

    /// <summary>
    /// Submit a cross-chain attestation with relayer signatures.
    /// Verified attestation results in a BST-VC credential.
    /// </summary>
    public ulong SubmitAttestation(
        uint sourceChainId,
        byte credentialType,
        byte[] claimData,
        byte[] attestationProof,
        byte[] relayerSignature,
        Address relayerAddress)
    {
        Require(_hasProfile.Get(Context.Sender), "NO_PROFILE");
        Require(_trustedRelayers.Get(relayerAddress), "UNTRUSTED_RELAYER");

        // Collect attestation fee
        Require(Context.TxValue >= _attestationFee.Get(), "INSUFFICIENT_FEE");

        // Verify the attestation
        var chainConfig = _chainConfigs.Get(sourceChainId);
        bool verified = false;

        if (chainConfig.VerificationType == 0)
        {
            // Relayer attestation: verify Ed25519 signature
            Hash256 attestationHash = ComputeAttestationHash(
                sourceChainId, credentialType, claimData, Context.Sender);
            verified = VerifyRelayerSignature(
                relayerAddress, attestationHash.ToArray(), relayerSignature);
        }
        else if (chainConfig.VerificationType == 1)
        {
            // Merkle proof: verify against stored state root
            verified = VerifyMerkleProof(sourceChainId, claimData, attestationProof);
        }
        else if (chainConfig.VerificationType == 2)
        {
            // ZK proof: verify Groth16 proof of state inclusion
            verified = VerifyZkStateProof(attestationProof, claimData);
        }

        Require(verified, "ATTESTATION_VERIFICATION_FAILED");

        // Issue BST-VC credential
        ulong credIndex = _credentialCount.Get(Context.Sender);
        Hash256 attestationHash2 = ComputeAttestationHash(
            sourceChainId, credentialType, claimData, Context.Sender);

        Require(!_issuedCredentials.Get(attestationHash2), "DUPLICATE_ATTESTATION");

        _credentials.Get(Context.Sender).Set(credIndex, new Credential
        {
            CredentialIndex = credIndex,
            SourceChainId = sourceChainId,
            CredentialType = credentialType,
            SchemaHash = GetSchemaForType(credentialType),
            ClaimData = claimData,
            AttestationHash = attestationHash2,
            IssuedAtBlock = Context.BlockNumber,
            ExpiryBlock = Context.BlockNumber + chainConfig.CredentialValidityBlocks,
            Revoked = false,
            Issuer = relayerAddress
        });

        _credentialCount.Set(Context.Sender, credIndex + 1);
        _issuedCredentials.Set(attestationHash2, true);

        // Update profile
        var profile = _profiles.Get(Context.Sender);
        profile.CredentialCount++;
        profile.LastUpdatedBlock = Context.BlockNumber;
        _profiles.Set(Context.Sender, profile);

        // Recalculate reputation
        RecalculateReputation(Context.Sender);

        EmitEvent("CredentialIssued", Context.Sender, credIndex, sourceChainId,
                  credentialType);
        return credIndex;
    }

    /// <summary>
    /// Submit an Ethereum-specific attestation via BridgeETH Merkle proof.
    /// </summary>
    public ulong SubmitEthereumAttestation(
        byte credentialType,
        byte[] ethereumAddress,
        byte[] stateProof,
        byte[] accountProof,
        ulong blockNumber)
    {
        // Verify Ethereum state root via BridgeETH
        bool verified = VerifyEthereumStateInclusion(
            ethereumAddress, stateProof, accountProof, blockNumber);
        Require(verified, "ETHEREUM_PROOF_FAILED");

        byte[] claimData = EncodeEthereumClaim(
            credentialType, ethereumAddress, blockNumber);
        return SubmitAttestationInternal(1, credentialType, claimData);
    }

    // --- ZK Identity Proofs ---

    /// <summary>
    /// Generate a ZK proof about aggregated identity without revealing specifics.
    /// Example: "I have credentials from 3+ chains with total DeFi activity > X"
    /// </summary>
    public byte[] GenerateAggregateIdentityProof(
        byte[] proofRequest,
        byte[] privateWitness)
    {
        Require(_hasProfile.Get(Context.Sender), "NO_PROFILE");

        // Generate Groth16 proof based on credential data
        byte[] proof = GenerateGroth16IdentityProof(
            Context.Sender, proofRequest, privateWitness);

        EmitEvent("IdentityProofGenerated", Context.Sender);
        return proof;
    }

    /// <summary>
    /// Verify a ZK identity proof. Called by protocols to check user claims.
    /// </summary>
    public bool VerifyAggregateIdentityProof(
        Address user,
        byte[] proofRequest,
        byte[] proof)
    {
        bool valid = VerifyGroth16IdentityProof(user, proofRequest, proof);
        EmitEvent("IdentityProofVerified", user, valid);
        return valid;
    }

    // --- Credential Management ---

    /// <summary>
    /// Revoke a credential (by the original attestation source only).
    /// </summary>
    public void RevokeCredential(Address profileOwner, ulong credentialIndex)
    {
        var credential = _credentials.Get(profileOwner).Get(credentialIndex);
        Require(Context.Sender == credential.Issuer, "NOT_ISSUER");
        Require(!credential.Revoked, "ALREADY_REVOKED");

        credential.Revoked = true;
        _credentials.Get(profileOwner).Set(credentialIndex, credential);

        RecalculateReputation(profileOwner);

        EmitEvent("CredentialRevoked", profileOwner, credentialIndex);
    }

    /// <summary>
    /// Renew an expiring credential with a fresh attestation.
    /// </summary>
    public void RenewCredential(
        ulong credentialIndex,
        byte[] freshAttestation,
        byte[] relayerSignature,
        Address relayerAddress)
    {
        Require(_hasProfile.Get(Context.Sender), "NO_PROFILE");
        var credential = _credentials.Get(Context.Sender).Get(credentialIndex);
        Require(!credential.Revoked, "CREDENTIAL_REVOKED");

        // Verify fresh attestation
        bool verified = VerifyRelayerSignature(
            relayerAddress, freshAttestation, relayerSignature);
        Require(verified, "INVALID_ATTESTATION");

        var chainConfig = _chainConfigs.Get(credential.SourceChainId);
        credential.ExpiryBlock = Context.BlockNumber + chainConfig.CredentialValidityBlocks;
        credential.IssuedAtBlock = Context.BlockNumber;
        _credentials.Get(Context.Sender).Set(credentialIndex, credential);

        EmitEvent("CredentialRenewed", Context.Sender, credentialIndex);
    }

    // --- Reputation Scoring ---

    /// <summary>
    /// Get the reputation score for a profile.
    /// </summary>
    public ReputationScore GetReputation(Address user) => _reputationScores.Get(user);

    /// <summary>
    /// Force reputation recalculation. Any user can trigger for their own profile.
    /// </summary>
    public void RefreshReputation()
    {
        Require(_hasProfile.Get(Context.Sender), "NO_PROFILE");
        RecalculateReputation(Context.Sender);
    }

    // --- Attestation Source Management ---

    /// <summary>
    /// Register a new attestation source. Governance-only.
    /// </summary>
    public uint RegisterAttestationSource(
        string name,
        uint chainId,
        Address sourceAddress,
        byte[] publicKey)
    {
        RequireGovernance();

        uint sourceId = _sourceCount.Get();
        _attestationSources.Set(sourceId, new AttestationSource
        {
            SourceId = sourceId,
            Name = name,
            ChainId = chainId,
            SourceAddress = sourceAddress,
            PublicKey = publicKey,
            Active = true,
            TotalAttestations = 0
        });
        _sourceCount.Set(sourceId + 1);
        _trustedRelayers.Set(sourceAddress, true);

        EmitEvent("AttestationSourceRegistered", sourceId, name, chainId);
        return sourceId;
    }

    /// <summary>
    /// Register a new chain identity configuration. Governance-only.
    /// </summary>
    public void RegisterChain(ChainIdentityConfig config)
    {
        RequireGovernance();
        _chainConfigs.Set(config.ChainId, config);
        EmitEvent("ChainRegistered", config.ChainId, config.ChainName);
    }

    // --- Query Methods ---

    public IdentityProfile GetProfile(Address user) => _profiles.Get(user);
    public bool HasProfile(Address user) => _hasProfile.Get(user);
    public ulong GetCredentialCount(Address user) => _credentialCount.Get(user);
    public Credential GetCredential(Address user, ulong index)
        => _credentials.Get(user).Get(index);
    public ulong GetProfileCount() => _profileCount.Get();
    public AttestationSource GetAttestationSource(uint sourceId)
        => _attestationSources.Get(sourceId);

    /// <summary>
    /// Check if a user has a valid (non-expired, non-revoked) credential of a type.
    /// </summary>
    public bool HasValidCredential(Address user, byte credentialType)
    {
        ulong count = _credentialCount.Get(user);
        for (ulong i = 0; i < count; i++)
        {
            var cred = _credentials.Get(user).Get(i);
            if (cred.CredentialType == credentialType &&
                !cred.Revoked &&
                cred.ExpiryBlock > Context.BlockNumber)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Count valid credentials from a specific chain.
    /// </summary>
    public ulong CountChainCredentials(Address user, uint chainId)
    {
        ulong count = 0;
        ulong total = _credentialCount.Get(user);
        for (ulong i = 0; i < total; i++)
        {
            var cred = _credentials.Get(user).Get(i);
            if (cred.SourceChainId == chainId && !cred.Revoked &&
                cred.ExpiryBlock > Context.BlockNumber)
            {
                count++;
            }
        }
        return count;
    }

    // --- Internal Helpers ---

    private void RecalculateReputation(Address user)
    {
        ulong credCount = _credentialCount.Get(user);
        ushort assetScore = 0, activityScore = 0, govScore = 0;
        ushort stakingScore = 0, identityScore = 0;

        for (ulong i = 0; i < credCount; i++)
        {
            var cred = _credentials.Get(user).Get(i);
            if (cred.Revoked || cred.ExpiryBlock <= Context.BlockNumber) continue;

            switch (cred.CredentialType)
            {
                case 0: assetScore += 50; break;      // asset ownership
                case 1: activityScore += 30; break;    // activity history
                case 2: identityScore += 100; break;   // KYC status
                case 3: govScore += 40; break;         // governance
                case 4: stakingScore += 60; break;     // staking
            }
        }

        // Cap components at 200 each
        if (assetScore > 200) assetScore = 200;
        if (activityScore > 200) activityScore = 200;
        if (govScore > 200) govScore = 200;
        if (stakingScore > 200) stakingScore = 200;
        if (identityScore > 200) identityScore = 200;

        ushort total = (ushort)(assetScore + activityScore + govScore +
                                stakingScore + identityScore);

        _reputationScores.Set(user, new ReputationScore
        {
            TotalScore = total,
            AssetScore = assetScore,
            ActivityScore = activityScore,
            GovernanceScore = govScore,
            StakingScore = stakingScore,
            IdentityScore = identityScore,
            LastComputedBlock = Context.BlockNumber
        });

        EmitEvent("ReputationUpdated", user, total);
    }

    private Hash256 ComputeAttestationHash(uint chainId, byte credType,
        byte[] claimData, Address user) { /* BLAKE3 hash */ }
    private bool VerifyRelayerSignature(Address relayer, byte[] msg,
        byte[] sig) { /* Ed25519 verify */ return true; }
    private bool VerifyMerkleProof(uint chainId, byte[] data,
        byte[] proof) { /* ... */ return true; }
    private bool VerifyZkStateProof(byte[] proof, byte[] data) { /* ... */ return true; }
    private bool VerifyEthereumStateInclusion(byte[] addr, byte[] stateProof,
        byte[] accountProof, ulong blockNum) { /* ... */ return true; }
    private byte[] GetSchemaForType(byte credentialType) { /* ... */ return new byte[0]; }
    private byte[] EncodeEthereumClaim(byte credType, byte[] addr,
        ulong blockNum) { /* ... */ return new byte[0]; }
    private ulong SubmitAttestationInternal(uint chainId, byte credType,
        byte[] claimData) { /* ... */ return 0; }
    private byte[] GenerateGroth16IdentityProof(Address user, byte[] request,
        byte[] witness) { /* ... */ return new byte[0]; }
    private bool VerifyGroth16IdentityProof(Address user, byte[] request,
        byte[] proof) { /* ... */ return true; }
    private void RequireGovernance() { /* ... */ }
}
```

## Complexity

**High** -- Cross-chain identity aggregation is among the most complex contracts in the proposal set. It requires: multi-chain attestation verification (each chain has different proof formats -- Merkle Patricia tries for Ethereum, Solana proofs, etc.), ZK proof generation and verification for privacy-preserving identity claims, credential lifecycle management (issuance, expiry, revocation, renewal), reputation scoring across heterogeneous data sources, and secure cross-chain communication for attestation delivery. The privacy requirements (preventing cross-chain address linking while enabling aggregate claims) add significant cryptographic complexity. Correct handling of credential validity, time bounds, and revocation across asynchronous systems is challenging.

## Priority

**P2** -- Cross-chain identity aggregation becomes valuable as Basalt's cross-chain bridge infrastructure matures and the ecosystem grows large enough to benefit from onboarding users from other chains. It depends on the Generic Cross-Chain Bridge (contract 68), BridgeETH, and the ZK compliance layer being stable. It should be developed after core DeFi and governance infrastructure but is strategically important for ecosystem growth and competitive differentiation.
