using Basalt.Sdk.Contracts;
using Basalt.Sdk.Contracts.Standards;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class CrossContractCallTests : IDisposable
{
    private readonly BasaltTestHost _host = new();

    [Fact]
    public void CrossContractCall_Invokes_Target_Method()
    {
        var tokenAddr = BasaltTestHost.CreateAddress(10);
        var token = new MintableBST20("Token", "TK", 18);
        _host.Deploy(tokenAddr, token);

        var alice = BasaltTestHost.CreateAddress(1);
        _host.SetCaller(alice);

        // Mint some tokens directly
        _host.Call(() => token.MintPublic(alice, 1000));

        // Call BalanceOf via cross-contract call
        var balance = Context.CallContract<ulong>(tokenAddr, "BalanceOf", alice);
        balance.Should().Be(1000);
    }

    [Fact]
    public void CrossContractCall_Reverts_On_Reentrancy()
    {
        var contractAddr = BasaltTestHost.CreateAddress(10);
        var contract = new ReentrantContract(contractAddr);
        _host.Deploy(contractAddr, contract);

        var caller = BasaltTestHost.CreateAddress(1);
        _host.SetCaller(caller);
        Context.Self = contractAddr; // Set self to contract addr

        var act = () => Context.CallContract(contractAddr, "Reenter");
        act.Should().Throw<ContractRevertException>().WithMessage("*Reentrancy detected*");
    }

    [Fact]
    public void CrossContractCall_Enforces_MaxCallDepth()
    {
        Context.CallDepth = Context.MaxCallDepth;

        var targetAddr = BasaltTestHost.CreateAddress(10);
        var act = () => Context.CallContract(targetAddr, "Foo");
        act.Should().Throw<ContractRevertException>().WithMessage("*Max call depth*");
    }

    [Fact]
    public void CrossContractCall_Reverts_On_Unknown_Contract()
    {
        var unknownAddr = BasaltTestHost.CreateAddress(99);
        var act = () => Context.CallContract(unknownAddr, "Foo");
        act.Should().Throw<ContractRevertException>().WithMessage("*Contract not found*");
    }

    [Fact]
    public void CrossContractCall_Restores_Context_After_Call()
    {
        var tokenAddr = BasaltTestHost.CreateAddress(10);
        var token = new MintableBST20("Token", "TK", 18);
        _host.Deploy(tokenAddr, token);

        var caller = BasaltTestHost.CreateAddress(1);
        _host.SetCaller(caller);

        var originalCaller = Context.Caller;
        var originalSelf = Context.Self;

        Context.CallContract<ulong>(tokenAddr, "TotalSupply");

        // Context should be restored after the call
        Context.Caller.Should().BeEquivalentTo(originalCaller);
        Context.Self.Should().BeEquivalentTo(originalSelf);
        Context.CallDepth.Should().Be(0);
    }

    public void Dispose() => _host.Dispose();
}

/// <summary>
/// Test contract that tries to re-enter itself.
/// </summary>
public class ReentrantContract
{
    private readonly byte[] _selfAddr;
    public ReentrantContract(byte[] selfAddr) => _selfAddr = selfAddr;

    public void Reenter()
    {
        Context.CallContract(_selfAddr, "Reenter");
    }
}
