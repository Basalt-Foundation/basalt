using Basalt.Core;
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
        var balance = Context.CallContract<UInt256>(tokenAddr, "BalanceOf", alice);
        balance.Should().Be((UInt256)1000);
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

        Context.CallContract<UInt256>(tokenAddr, "TotalSupply");

        // Context should be restored after the call
        Context.Caller.Should().BeEquivalentTo(originalCaller);
        Context.Self.Should().BeEquivalentTo(originalSelf);
        Context.CallDepth.Should().Be(0);
    }

    [Fact]
    public void ReentrantCallback_IsStaticCall_BlocksWrites()
    {
        // A (TokenContract) calls B (PolicyContract), B calls back A.BalanceOf (read — OK)
        var tokenAddr = BasaltTestHost.CreateAddress(10);
        var policyAddr = BasaltTestHost.CreateAddress(11);

        var token = new MintableBST20("Token", "TK", 18);
        var policy = new CallbackPolicyContract(tokenAddr);
        _host.Deploy(tokenAddr, token);
        _host.Deploy(policyAddr, policy);

        var caller = BasaltTestHost.CreateAddress(1);
        _host.SetCaller(caller);
        _host.Call(() => token.MintPublic(caller, 1000));

        // Token calls Policy.CheckBalance which calls back Token.BalanceOf (read)
        Context.Self = tokenAddr;
        var balance = Context.CallContract<UInt256>(policyAddr, "CheckBalance", caller);
        balance.Should().Be((UInt256)1000);
    }

    [Fact]
    public void ReentrantCallback_IsStaticCall_WritesRevert()
    {
        // A calls B, B calls back A with a method that writes — should revert
        var contractAAddr = BasaltTestHost.CreateAddress(10);
        var contractBAddr = BasaltTestHost.CreateAddress(11);

        var contractA = new WritableContract();
        var contractB = new CallbackWriterContract(contractAAddr);
        _host.Deploy(contractAAddr, contractA);
        _host.Deploy(contractBAddr, contractB);

        var caller = BasaltTestHost.CreateAddress(1);
        _host.SetCaller(caller);
        Context.Self = contractAAddr;

        // A calls B.TriggerWriteBack, which calls A.WriteState — should fail with static call error
        var act = () => Context.CallContract(contractBAddr, "TriggerWriteBack");
        act.Should().Throw<ContractRevertException>()
            .WithMessage("*Static call*");
    }

    [Fact]
    public void IsStaticCall_RestoredAfterCallback()
    {
        var tokenAddr = BasaltTestHost.CreateAddress(10);
        var policyAddr = BasaltTestHost.CreateAddress(11);

        var token = new MintableBST20("Token", "TK", 18);
        var policy = new CallbackPolicyContract(tokenAddr);
        _host.Deploy(tokenAddr, token);
        _host.Deploy(policyAddr, policy);

        var caller = BasaltTestHost.CreateAddress(1);
        _host.SetCaller(caller);
        Context.Self = tokenAddr;

        Context.IsStaticCall.Should().BeFalse();
        Context.CallContract<UInt256>(policyAddr, "CheckBalance", caller);
        Context.IsStaticCall.Should().BeFalse(); // Restored after call chain
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

/// <summary>
/// Test policy contract that calls back into the token to read BalanceOf.
/// </summary>
public class CallbackPolicyContract
{
    private readonly byte[] _tokenAddr;
    public CallbackPolicyContract(byte[] tokenAddr) => _tokenAddr = tokenAddr;

    public UInt256 CheckBalance(byte[] account)
    {
        // This is a callback from token→policy→token.BalanceOf (read-only)
        return Context.CallContract<UInt256>(_tokenAddr, "BalanceOf", account);
    }
}

/// <summary>
/// Test contract that calls back into the caller and attempts a storage write.
/// </summary>
public class CallbackWriterContract
{
    private readonly byte[] _targetAddr;
    public CallbackWriterContract(byte[] targetAddr) => _targetAddr = targetAddr;

    public void TriggerWriteBack()
    {
        Context.CallContract(_targetAddr, "WriteState");
    }
}

/// <summary>
/// Test contract with a write method (used to verify static call blocks writes).
/// </summary>
public class WritableContract
{
    public void WriteState()
    {
        ContractStorage.Set("test_key", "test_value");
    }
}
