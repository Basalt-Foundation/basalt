using Basalt.Core;
using Basalt.Execution.VM;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

public class GenesisContractDeployerTests
{
    private static Hash256 ManifestKey()
    {
        Span<byte> k = stackalloc byte[32];
        k.Clear();
        k[0] = 0xFF;
        k[1] = 0x01;
        return new Hash256(k);
    }

    [Fact]
    public void DeployAll_Deploys_TrilithAnchor_At_Its_System_Address()
    {
        var stateDb = new InMemoryStateDb();
        GenesisContractDeployer.DeployAll(stateDb, 31337);

        var account = stateDb.GetAccount(GenesisContractDeployer.Addresses.TrilithAnchor);
        account.Should().NotBeNull();
        account!.Value.AccountType.Should().Be(AccountType.SystemContract);

        var manifest = stateDb.GetStorage(GenesisContractDeployer.Addresses.TrilithAnchor, ManifestKey());
        manifest.Should().NotBeNull();
        ContractRegistry.IsSdkContract(manifest!).Should().BeTrue();
        var (typeId, _) = ContractRegistry.ParseManifest(manifest!);
        typeId.Should().Be((ushort)0x0109);
    }

    [Fact]
    public void DeployAll_Still_Deploys_The_Name_Service()
    {
        var stateDb = new InMemoryStateDb();
        GenesisContractDeployer.DeployAll(stateDb, 31337);

        var account = stateDb.GetAccount(GenesisContractDeployer.Addresses.NameService);
        account.Should().NotBeNull();
        account!.Value.AccountType.Should().Be(AccountType.SystemContract);
    }
}
