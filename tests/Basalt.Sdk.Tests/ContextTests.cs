using Basalt.Sdk.Contracts;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class ContextTests : IDisposable
{
    private readonly BasaltTestHost _host = new();

    [Fact]
    public void Require_TrueCondition_DoesNotThrow()
    {
        var act = () => Context.Require(true, "should not throw");
        act.Should().NotThrow();
    }

    [Fact]
    public void Require_FalseCondition_ThrowsRevertException()
    {
        var act = () => Context.Require(false, "condition failed");
        act.Should().Throw<ContractRevertException>()
            .WithMessage("condition failed");
    }

    [Fact]
    public void Require_FalseCondition_DefaultMessage()
    {
        var act = () => Context.Require(false);
        act.Should().Throw<ContractRevertException>()
            .WithMessage("Require failed");
    }

    [Fact]
    public void Revert_AlwaysThrows()
    {
        var act = () => Context.Revert("explicit revert");
        act.Should().Throw<ContractRevertException>()
            .WithMessage("explicit revert");
    }

    [Fact]
    public void Revert_DefaultMessage()
    {
        var act = () => Context.Revert();
        act.Should().Throw<ContractRevertException>()
            .WithMessage("Reverted");
    }

    [Fact]
    public void Emit_InvokesHandler()
    {
        string? capturedName = null;
        object? capturedEvent = null;
        Context.EventEmitted = (name, evt) =>
        {
            capturedName = name;
            capturedEvent = evt;
        };

        var testEvent = new TestEvent { Value = 42 };
        Context.Emit(testEvent);

        capturedName.Should().Be("TestEvent");
        capturedEvent.Should().BeSameAs(testEvent);
    }

    [Fact]
    public void Emit_NullHandler_DoesNotThrow()
    {
        Context.EventEmitted = null;
        // Emit with null handler should not throw (it will actually throw a NullReferenceException
        // because the code calls EventEmitted?.Invoke, but let's verify the ?. pattern)
        // Actually, looking at the code: EventEmitted?.Invoke should be safe.
        var act = () => Context.Emit(new TestEvent { Value = 1 });
        act.Should().NotThrow();
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        // Set up non-default state
        Context.Caller = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };
        Context.Self = new byte[] { 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };
        Context.TxValue = 12345;
        Context.BlockTimestamp = 99999;
        Context.BlockHeight = 500;
        Context.ChainId = 42;
        Context.GasRemaining = 1_000_000;
        Context.CallDepth = 3;
        Context.ReentrancyGuard.Add("some_address");
        Context.EventEmitted = (_, _) => { };
        Context.CrossContractCallHandler = (_, _, _) => null;

        Context.Reset();

        Context.Caller.Should().BeEquivalentTo(new byte[20]);
        Context.Self.Should().BeEquivalentTo(new byte[20]);
        Context.TxValue.Should().Be(0);
        Context.BlockTimestamp.Should().Be(0);
        Context.BlockHeight.Should().Be(0UL);
        Context.ChainId.Should().Be(0U);
        Context.GasRemaining.Should().Be(0UL);
        Context.CallDepth.Should().Be(0);
        Context.ReentrancyGuard.Should().BeEmpty();
        Context.EventEmitted.Should().BeNull();
        Context.CrossContractCallHandler.Should().BeNull();
    }

    [Fact]
    public void CallDepth_EnforcesMaxDepth()
    {
        // Set call depth to the maximum
        Context.CallDepth = Context.MaxCallDepth;
        Context.CrossContractCallHandler = (_, _, _) => null;

        var targetAddr = BasaltTestHost.CreateAddress(10);
        var act = () => Context.CallContract(targetAddr, "SomeMethod");
        act.Should().Throw<ContractRevertException>()
            .WithMessage("*Max call depth*");
    }

    [Fact]
    public void CallDepth_AllowsCallsBelowMax()
    {
        var targetAddr = BasaltTestHost.CreateAddress(10);
        var token = new MintableBST20("T", "T", 18);
        _host.Deploy(targetAddr, token);

        Context.CallDepth = Context.MaxCallDepth - 1;
        // This should not throw since we're just below the limit
        var act = () => Context.CallContract<ulong>(targetAddr, "TotalSupply");
        act.Should().NotThrow();
    }

    [Fact]
    public void MaxCallDepth_IsEight()
    {
        Context.MaxCallDepth.Should().Be(8);
    }

    [Fact]
    public void ReentrancyGuard_PreventsReentrantCalls()
    {
        var contractAddr = BasaltTestHost.CreateAddress(10);
        var contract = new ReentrantContract(contractAddr);
        _host.Deploy(contractAddr, contract);

        _host.SetCaller(BasaltTestHost.CreateAddress(1));
        Context.Self = contractAddr;

        var act = () => Context.CallContract(contractAddr, "Reenter");
        act.Should().Throw<ContractRevertException>()
            .WithMessage("*Reentrancy detected*");
    }

    [Fact]
    public void CrossContractCall_RestoresContextAfterCall()
    {
        var tokenAddr = BasaltTestHost.CreateAddress(10);
        var token = new MintableBST20("T", "T", 18);
        _host.Deploy(tokenAddr, token);

        var caller = BasaltTestHost.CreateAddress(1);
        _host.SetCaller(caller);
        var originalSelf = Context.Self;

        Context.CallContract<ulong>(tokenAddr, "TotalSupply");

        Context.Caller.Should().BeEquivalentTo(caller);
        Context.Self.Should().BeEquivalentTo(originalSelf);
        Context.CallDepth.Should().Be(0);
    }

    [Fact]
    public void Caller_And_Self_DefaultToZeroAddress()
    {
        Context.Reset();
        Context.Caller.Should().BeEquivalentTo(new byte[20]);
        Context.Self.Should().BeEquivalentTo(new byte[20]);
    }

    [Fact]
    public void BlockTimestamp_And_BlockHeight_Settable()
    {
        _host.SetBlockTimestamp(123456);
        _host.SetBlockHeight(789);

        Context.BlockTimestamp.Should().Be(123456);
        Context.BlockHeight.Should().Be(789UL);
    }

    public void Dispose() => _host.Dispose();
}

public class TestEvent
{
    public int Value { get; init; }
}
