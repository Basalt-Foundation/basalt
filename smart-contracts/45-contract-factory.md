# Contract Factory

## Category

Infrastructure / Developer Tools

## Summary

A contract factory that deploys parameterized contract instances from pre-registered templates, maintaining a registry of all deployed instances. It enables one-click token launches, pool creation, vault deployment, and other common contract deployment patterns without requiring users to understand bytecode or deployment mechanics. The factory supports template versioning, deployment fees, and per-template access control, significantly reducing deployment friction for both developers and end users.

## Why It's Useful

- **One-click token creation**: Non-technical users can launch BST-20, BST-721, or BST-1155 tokens by providing parameters (name, symbol, supply) without writing or deploying code.
- **Standardized deployments**: All instances from a template share the same audited codebase, reducing the risk of bugs from custom deployments. Users get security by default.
- **Discovery and registry**: The factory maintains a registry of all deployed instances, making it easy for explorers, indexers, and dApps to discover new contracts.
- **Reduced deployment costs**: Template bytecode is stored once; deploying instances only requires parameter initialization, reducing gas costs.
- **Template marketplace**: Developers can publish templates and earn deployment fees, creating an economy around reusable contract patterns.
- **Version management**: Templates can be versioned, allowing upgrades while maintaining backward compatibility. Users can choose which version to deploy.
- **Protocol bootstrapping**: New protocols can launch with a set of factory-deployed contracts (token + pool + vault) in a single transaction batch.

## Key Features

- Template registration: developers register contract templates with a type ID (matching Basalt's contract type ID system), manifest, description, and deployment fee
- Template versioning: each template can have multiple versions. New deployments use the latest version by default, but users can specify a specific version.
- Parameterized deployment: users deploy instances by specifying the template ID and constructor parameters. The factory handles manifest construction (0xBA5A magic + type ID + params) and deployment.
- Instance registry: all deployed instances are tracked with (templateId, version, deployer, address, blockHeight)
- Deployment fee: template authors can set a per-deployment fee that is paid by the deployer and forwarded to the template author
- Access control: templates can be public (anyone can deploy) or restricted (only whitelisted addresses)
- Batch deployment: deploy multiple instances in a single call (e.g., create a token + create a pool + create a vault)
- Template categories: templates are organized by category (token, DeFi, governance, utility) for discovery
- Template deprecation: authors can deprecate old template versions to prevent new deployments
- Instance metadata: deployed instances can have optional metadata (name, description, icon URL) for explorer display

## Basalt-Specific Advantages

- **Native AOT contract deployment**: Basalt's SDK contract system uses magic bytes `[0xBA, 0x5A]` followed by a 2-byte type ID and constructor arguments. The factory constructs this manifest directly, leveraging the existing deployment infrastructure without any bytecode manipulation.
- **Source-generated dispatch**: All SDK contracts use source-generated dispatch (not reflection), so factory-deployed instances are AOT-safe by construction. There is no risk of deploying contracts that fail under AOT compilation.
- **ContractRegistry integration**: The factory interacts with Basalt's `ContractRegistry` which maps type IDs (0x0001-0x0007 for standards, 0x0100+ for system contracts) to contract implementations. New factory templates are registered with unique type IDs.
- **BNS name assignment**: Deployed instances can optionally be assigned a BNS name (e.g., "mytoken.bst") via cross-contract call to BNS during deployment, providing human-readable addresses from the start.
- **ZK compliance for restricted templates**: Templates that deploy compliance-sensitive contracts (e.g., security tokens) can require deployers to present valid ZK compliance proofs before deployment.
- **Cross-contract initialization**: After deployment, the factory can execute initialization calls on the new instance (e.g., minting initial supply, setting up roles) in the same transaction, ensuring atomic setup.
- **Governance-controlled template approval**: Template registration can be gated behind governance approval, ensuring that only audited and approved templates are available in the factory.

## Token Standards Used

- **BST-20**: Primary template type -- one-click fungible token launches
- **BST-721**: One-click NFT collection creation
- **BST-1155**: Multi-token contract deployment
- **BST-3525**: Semi-fungible token deployment for structured finance products
- **BST-4626**: Yield vault deployment with configurable strategies

## Integration Points

- **BNS (0x...1002)**: Optional BNS name assignment for deployed instances.
- **Governance (0x...1005 area)**: Template approval can be governance-gated. Factory parameters (deployment fees, access control) can be updated via governance.
- **SchemaRegistry (0x...1006)**: Compliance verification for restricted template deployments.
- **StakingPool (0x...1005)**: Factory can deploy StakingPool-compatible contracts.
- **Escrow (0x...1003)**: Factory can deploy Escrow instances for specific use cases.

## Technical Sketch

```csharp
using Basalt.Core;

namespace Basalt.Sdk.Contracts.Standards;

/// <summary>
/// Contract Factory -- deploy parameterized contract instances from pre-registered
/// templates. Maintains a registry of all deployed instances for discovery.
/// </summary>
[BasaltContract]
public partial class ContractFactory
{
    // --- Template registry ---
    private readonly StorageValue<ulong> _nextTemplateId;
    private readonly StorageMap<string, string> _templateNames;          // templateId -> name
    private readonly StorageMap<string, string> _templateDescriptions;   // templateId -> description
    private readonly StorageMap<string, string> _templateAuthors;        // templateId -> author hex
    private readonly StorageMap<string, string> _templateCategories;     // templateId -> category
    private readonly StorageMap<string, UInt256> _templateDeployFees;    // templateId -> fee
    private readonly StorageMap<string, bool> _templatePublic;           // templateId -> isPublic
    private readonly StorageMap<string, uint> _templateLatestVersion;    // templateId -> latest version
    private readonly StorageMap<string, bool> _templateDeprecated;       // "templateId:version" -> deprecated

    // --- Template bytecode / type ID ---
    private readonly StorageMap<string, uint> _templateTypeIds;          // "templateId:version" -> Basalt type ID
    private readonly StorageMap<string, string> _templateManifestPrefixes; // "templateId:version" -> hex-encoded manifest prefix

    // --- Access control ---
    private readonly StorageMap<string, bool> _templateWhitelist;       // "templateId:deployerHex" -> allowed

    // --- Instance registry ---
    private readonly StorageValue<ulong> _nextInstanceId;
    private readonly StorageMap<string, ulong> _instanceTemplateIds;    // instanceId -> templateId
    private readonly StorageMap<string, uint> _instanceVersions;        // instanceId -> version
    private readonly StorageMap<string, string> _instanceDeployers;     // instanceId -> deployer hex
    private readonly StorageMap<string, string> _instanceAddresses;     // instanceId -> contract address hex
    private readonly StorageMap<string, ulong> _instanceDeployBlocks;   // instanceId -> deploy block
    private readonly StorageMap<string, string> _instanceMetadata;      // instanceId -> metadata string

    // --- Per-template instance tracking ---
    private readonly StorageMap<string, ulong> _templateInstanceCount;  // templateId -> count

    // --- Admin ---
    private readonly StorageMap<string, string> _admin;
    private readonly StorageValue<bool> _requireGovernanceApproval;
    private readonly byte[] _governanceAddress;

    public ContractFactory(byte[] governanceAddress, bool requireGovernanceApproval = false)
    {
        _governanceAddress = governanceAddress;

        _nextTemplateId = new StorageValue<ulong>("cf_ntpl");
        _templateNames = new StorageMap<string, string>("cf_tname");
        _templateDescriptions = new StorageMap<string, string>("cf_tdesc");
        _templateAuthors = new StorageMap<string, string>("cf_tauth");
        _templateCategories = new StorageMap<string, string>("cf_tcat");
        _templateDeployFees = new StorageMap<string, UInt256>("cf_tfee");
        _templatePublic = new StorageMap<string, bool>("cf_tpub");
        _templateLatestVersion = new StorageMap<string, uint>("cf_tver");
        _templateDeprecated = new StorageMap<string, bool>("cf_tdep");
        _templateTypeIds = new StorageMap<string, uint>("cf_ttid");
        _templateManifestPrefixes = new StorageMap<string, string>("cf_tmanif");
        _templateWhitelist = new StorageMap<string, bool>("cf_twl");
        _nextInstanceId = new StorageValue<ulong>("cf_ninst");
        _instanceTemplateIds = new StorageMap<string, ulong>("cf_itpl");
        _instanceVersions = new StorageMap<string, uint>("cf_iver");
        _instanceDeployers = new StorageMap<string, string>("cf_idep");
        _instanceAddresses = new StorageMap<string, string>("cf_iaddr");
        _instanceDeployBlocks = new StorageMap<string, ulong>("cf_iblk");
        _instanceMetadata = new StorageMap<string, string>("cf_imeta");
        _templateInstanceCount = new StorageMap<string, ulong>("cf_ticnt");
        _admin = new StorageMap<string, string>("cf_admin");
        _requireGovernanceApproval = new StorageValue<bool>("cf_govreq");

        _admin.Set("admin", Convert.ToHexString(Context.Caller));
        _requireGovernanceApproval.Set(requireGovernanceApproval);
    }

    // ===================== Template Management =====================

    /// <summary>
    /// Register a new contract template. Returns the template ID.
    /// </summary>
    [BasaltEntrypoint]
    public ulong RegisterTemplate(string name, string description, string category,
        uint typeId, UInt256 deployFee, bool isPublic)
    {
        Context.Require(!string.IsNullOrEmpty(name), "FACTORY: name required");
        Context.Require(typeId > 0, "FACTORY: invalid type ID");

        var id = _nextTemplateId.Get();
        _nextTemplateId.Set(id + 1);
        var key = id.ToString();

        _templateNames.Set(key, name);
        _templateDescriptions.Set(key, description);
        _templateAuthors.Set(key, Convert.ToHexString(Context.Caller));
        _templateCategories.Set(key, category);
        _templateDeployFees.Set(key, deployFee);
        _templatePublic.Set(key, isPublic);

        // Version 1
        _templateLatestVersion.Set(key, 1);
        var versionKey = key + ":1";
        _templateTypeIds.Set(versionKey, typeId);

        Context.Emit(new TemplateRegisteredEvent
        {
            TemplateId = id, Name = name, Author = Context.Caller,
            Category = category, TypeId = typeId
        });
        return id;
    }

    /// <summary>
    /// Publish a new version of an existing template.
    /// </summary>
    [BasaltEntrypoint]
    public uint PublishVersion(ulong templateId, uint newTypeId)
    {
        RequireTemplateAuthor(templateId);
        var key = templateId.ToString();
        var version = _templateLatestVersion.Get(key) + 1;
        _templateLatestVersion.Set(key, version);

        var versionKey = key + ":" + version.ToString();
        _templateTypeIds.Set(versionKey, newTypeId);

        Context.Emit(new TemplateVersionPublishedEvent
        {
            TemplateId = templateId, Version = version, TypeId = newTypeId
        });
        return version;
    }

    /// <summary>
    /// Deprecate a specific template version (prevents new deployments).
    /// </summary>
    [BasaltEntrypoint]
    public void DeprecateVersion(ulong templateId, uint version)
    {
        RequireTemplateAuthor(templateId);
        _templateDeprecated.Set(templateId.ToString() + ":" + version.ToString(), true);

        Context.Emit(new TemplateDeprecatedEvent
        {
            TemplateId = templateId, Version = version
        });
    }

    /// <summary>
    /// Add an address to a template's deployment whitelist (for non-public templates).
    /// </summary>
    [BasaltEntrypoint]
    public void WhitelistDeployer(ulong templateId, byte[] deployer)
    {
        RequireTemplateAuthor(templateId);
        _templateWhitelist.Set(templateId.ToString() + ":" + Convert.ToHexString(deployer), true);
    }

    /// <summary>
    /// Update the deployment fee for a template.
    /// </summary>
    [BasaltEntrypoint]
    public void SetDeployFee(ulong templateId, UInt256 newFee)
    {
        RequireTemplateAuthor(templateId);
        _templateDeployFees.Set(templateId.ToString(), newFee);
    }

    // ===================== Deployment =====================

    /// <summary>
    /// Deploy a new contract instance from a template.
    /// Send the deployment fee (if any) as value.
    /// Returns the instance ID.
    /// </summary>
    [BasaltEntrypoint]
    public ulong Deploy(ulong templateId, uint version, string metadata)
    {
        var tplKey = templateId.ToString();
        Context.Require(!string.IsNullOrEmpty(_templateNames.Get(tplKey)), "FACTORY: template not found");

        if (version == 0) version = _templateLatestVersion.Get(tplKey);
        var versionKey = tplKey + ":" + version.ToString();
        Context.Require(!_templateDeprecated.Get(versionKey), "FACTORY: version deprecated");

        // Access control
        var isPublic = _templatePublic.Get(tplKey);
        if (!isPublic)
        {
            var callerHex = Convert.ToHexString(Context.Caller);
            Context.Require(
                _templateWhitelist.Get(tplKey + ":" + callerHex),
                "FACTORY: not whitelisted");
        }

        // Deployment fee
        var fee = _templateDeployFees.Get(tplKey);
        if (!fee.IsZero)
        {
            Context.Require(Context.TxValue >= fee, "FACTORY: insufficient deployment fee");
            var author = Convert.FromHexString(_templateAuthors.Get(tplKey));
            Context.TransferNative(author, fee);
        }

        // Record instance
        var instanceId = _nextInstanceId.Get();
        _nextInstanceId.Set(instanceId + 1);
        var instKey = instanceId.ToString();

        _instanceTemplateIds.Set(instKey, templateId);
        _instanceVersions.Set(instKey, version);
        _instanceDeployers.Set(instKey, Convert.ToHexString(Context.Caller));
        _instanceDeployBlocks.Set(instKey, Context.BlockHeight);
        _instanceMetadata.Set(instKey, metadata);

        var count = _templateInstanceCount.Get(tplKey);
        _templateInstanceCount.Set(tplKey, count + 1);

        Context.Emit(new InstanceDeployedEvent
        {
            InstanceId = instanceId, TemplateId = templateId,
            Version = version, Deployer = Context.Caller,
            DeployBlock = Context.BlockHeight
        });
        return instanceId;
    }

    /// <summary>
    /// Record the deployed contract address after deployment completes.
    /// Called by the deployer to link instanceId to the on-chain address.
    /// </summary>
    [BasaltEntrypoint]
    public void RecordAddress(ulong instanceId, byte[] contractAddress)
    {
        var key = instanceId.ToString();
        Context.Require(
            Convert.ToHexString(Context.Caller) == _instanceDeployers.Get(key),
            "FACTORY: not deployer");
        Context.Require(contractAddress.Length == 20, "FACTORY: invalid address");

        _instanceAddresses.Set(key, Convert.ToHexString(contractAddress));

        Context.Emit(new InstanceAddressRecordedEvent
        {
            InstanceId = instanceId, ContractAddress = contractAddress
        });
    }

    // ===================== Views =====================

    [BasaltView]
    public string GetTemplateName(ulong templateId) => _templateNames.Get(templateId.ToString()) ?? "";

    [BasaltView]
    public string GetTemplateCategory(ulong templateId) => _templateCategories.Get(templateId.ToString()) ?? "";

    [BasaltView]
    public uint GetLatestVersion(ulong templateId) => _templateLatestVersion.Get(templateId.ToString());

    [BasaltView]
    public UInt256 GetDeployFee(ulong templateId) => _templateDeployFees.Get(templateId.ToString());

    [BasaltView]
    public ulong GetTemplateInstanceCount(ulong templateId) => _templateInstanceCount.Get(templateId.ToString());

    [BasaltView]
    public ulong GetInstanceTemplateId(ulong instanceId) => _instanceTemplateIds.Get(instanceId.ToString());

    [BasaltView]
    public string GetInstanceAddress(ulong instanceId) => _instanceAddresses.Get(instanceId.ToString()) ?? "";

    [BasaltView]
    public string GetInstanceDeployer(ulong instanceId) => _instanceDeployers.Get(instanceId.ToString()) ?? "";

    [BasaltView]
    public string GetInstanceMetadata(ulong instanceId) => _instanceMetadata.Get(instanceId.ToString()) ?? "";

    [BasaltView]
    public ulong GetTotalInstances() => _nextInstanceId.Get();

    [BasaltView]
    public ulong GetTotalTemplates() => _nextTemplateId.Get();

    // ===================== Internal =====================

    private void RequireTemplateAuthor(ulong templateId)
    {
        Context.Require(
            Convert.ToHexString(Context.Caller) == _templateAuthors.Get(templateId.ToString()),
            "FACTORY: not template author");
    }
}

// ===================== Events =====================

[BasaltEvent]
public class TemplateRegisteredEvent
{
    [Indexed] public ulong TemplateId { get; set; }
    public string Name { get; set; } = "";
    [Indexed] public byte[] Author { get; set; } = null!;
    public string Category { get; set; } = "";
    public uint TypeId { get; set; }
}

[BasaltEvent]
public class TemplateVersionPublishedEvent
{
    [Indexed] public ulong TemplateId { get; set; }
    public uint Version { get; set; }
    public uint TypeId { get; set; }
}

[BasaltEvent]
public class TemplateDeprecatedEvent
{
    [Indexed] public ulong TemplateId { get; set; }
    public uint Version { get; set; }
}

[BasaltEvent]
public class InstanceDeployedEvent
{
    [Indexed] public ulong InstanceId { get; set; }
    [Indexed] public ulong TemplateId { get; set; }
    public uint Version { get; set; }
    [Indexed] public byte[] Deployer { get; set; } = null!;
    public ulong DeployBlock { get; set; }
}

[BasaltEvent]
public class InstanceAddressRecordedEvent
{
    [Indexed] public ulong InstanceId { get; set; }
    public byte[] ContractAddress { get; set; } = null!;
}
```

## Complexity

**Medium** -- The factory pattern is well-understood and the core logic (template registration, version management, deployment with fee, instance tracking) is mostly storage bookkeeping. The main complexity is in correctly constructing the Basalt manifest bytes (`[0xBA, 0x5A][typeId][params]`) from template data and user-provided parameters, and in the two-step deployment flow (deploy contract off-chain, then record the address via RecordAddress). Template versioning and deprecation add moderate state management complexity.

## Priority

**P1** -- A contract factory is important for ecosystem growth because it dramatically lowers the barrier to deploying contracts. One-click token launches, in particular, are a proven driver of chain adoption. The factory should be available shortly after mainnet launch to enable the first wave of token creators, NFT collections, and DeFi protocols.
