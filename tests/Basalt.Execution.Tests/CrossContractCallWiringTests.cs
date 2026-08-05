using Basalt.Codec;
using Basalt.Core;
using Basalt.Execution.VM;
using Basalt.Sdk.Contracts;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

/// <summary>
/// Whether one contract can call another when the real runtime is the one executing it.
///
/// Worth its own file because the SDK test host installs a cross-contract handler of its own that
/// invokes the target's C# method directly. Every composed contract therefore passes its unit tests
/// whether or not a validator could run it. These go through <see cref="ManagedContractRuntime"/>, which
/// is what a validator actually runs.
/// </summary>
public class CrossContractCallWiringTests
{
    private readonly InMemoryStateDb _stateDb = new();

    private static readonly Address VaultAddr = new(Enumerable.Repeat((byte)0x11, 20).ToArray());
    private static readonly Address TokenAddr = new(Enumerable.Repeat((byte)0x22, 20).ToArray());
    private static readonly Address HolderAddr = new(Enumerable.Repeat((byte)0x33, 20).ToArray());

    private static readonly UInt256 Supply = new(1_000_000);
    private static readonly UInt256 DepositAmount = new(1_000);

    private VmExecutionContext Context(Address contract, Address caller) => new()
    {
        Caller = caller,
        ContractAddress = contract,
        Value = UInt256.Zero,
        BlockTimestamp = 1_700_000_000_000,
        BlockNumber = 42,
        BlockProposer = new Address(new byte[20]),
        ChainId = 4242,
        GasMeter = new GasMeter(50_000_000),
        StateDb = _stateDb,
        CallDepth = 0,
    };

    private static Hash256 CodeKey()
    {
        var key = new byte[32];
        key[0] = 0xFF;
        key[1] = 0x01;
        return new Hash256(key);
    }

    private byte[] Deploy(Address at, Address deployer, ushort typeId, byte[] ctorArgs)
    {
        var manifest = ContractRegistry.BuildManifest(typeId, ctorArgs);
        new ManagedContractRuntime().Deploy(manifest, [], Context(at, deployer));
        return _stateDb.GetStorage(at, CodeKey())!;
    }

    // BasaltWriter is a struct, so every buffer is built inline rather than through a helper that would
    // write into a copy and hand back an empty array.
    private static byte[] TokenArgs(UInt256 initialSupply)
    {
        var buffer = new byte[256];
        var writer = new BasaltWriter(buffer);
        writer.WriteString("Underlying");
        writer.WriteString("UND");
        writer.WriteByte(18);
        writer.WriteUInt256(initialSupply);
        return buffer[..writer.Position];
    }

    private static byte[] VaultArgs(Address asset)
    {
        var buffer = new byte[256];
        var writer = new BasaltWriter(buffer);
        writer.WriteString("Vault");
        writer.WriteString("vUND");
        writer.WriteByte(18);
        writer.WriteBytes(asset.ToArray());
        return buffer[..writer.Position];
    }

    private static byte[] ApproveCall(Address spender, UInt256 amount)
    {
        var buffer = new byte[256];
        var writer = new BasaltWriter(buffer);
        writer.WriteBytes(spender.ToArray());
        writer.WriteUInt256(amount);
        return WithSelector("Approve", buffer, writer.Position);
    }

    private static byte[] DepositCall(UInt256 assets)
    {
        var buffer = new byte[64];
        var writer = new BasaltWriter(buffer);
        writer.WriteUInt256(assets);
        return WithSelector("Deposit", buffer, writer.Position);
    }

    private static byte[] BalanceOfCall(Address account)
    {
        var buffer = new byte[64];
        var writer = new BasaltWriter(buffer);
        writer.WriteBytes(account.ToArray());
        return WithSelector("BalanceOf", buffer, writer.Position);
    }

    private static byte[] WithSelector(string method, byte[] args, int argsLength)
    {
        var selector = SelectorHelper.ComputeSelectorBytes(method);
        var callData = new byte[selector.Length + argsLength];
        selector.CopyTo(callData, 0);
        args.AsSpan(0, argsLength).CopyTo(callData.AsSpan(selector.Length));
        return callData;
    }

    /// <summary>
    /// A vault deposit moves the underlying token, which it can only do by calling the token contract
    /// and believing what TransferFrom returns. That makes it the smallest honest end-to-end check: the
    /// call has to be placed, dispatched, and its return value decoded, or the deposit is wrong.
    /// </summary>
    [Fact]
    public void A_contract_can_call_another_contract_under_the_production_runtime()
    {
        byte[] tokenCode = Deploy(TokenAddr, HolderAddr, 0x0001, TokenArgs(Supply));
        byte[] vaultCode = Deploy(VaultAddr, HolderAddr, 0x0006, VaultArgs(TokenAddr));

        var runtime = new ManagedContractRuntime();

        ContractCallResult approve = runtime.Execute(
            tokenCode, ApproveCall(VaultAddr, DepositAmount), Context(TokenAddr, HolderAddr));
        approve.Success.Should().BeTrue(approve.ErrorMessage);

        ContractCallResult deposit = runtime.Execute(
            vaultCode, DepositCall(DepositAmount), Context(VaultAddr, HolderAddr));

        deposit.Success.Should().BeTrue(
            "a validator runs this runtime, so a cross-contract call that only works under the SDK test "
            + "host works nowhere: {0}", deposit.ErrorMessage);
    }

    /// <summary>
    /// The call has to actually move the tokens, not merely return without complaining. Deposit reads
    /// TransferFrom's return value and reverts on false, so a handler returning a default false would
    /// fail the test above, while one returning a default true would pass it and mint shares against
    /// assets that never arrived. Only the balances settle which of those happened.
    /// </summary>
    [Fact]
    public void The_call_moves_state_in_the_contract_that_was_called()
    {
        byte[] tokenCode = Deploy(TokenAddr, HolderAddr, 0x0001, TokenArgs(Supply));
        byte[] vaultCode = Deploy(VaultAddr, HolderAddr, 0x0006, VaultArgs(TokenAddr));

        var runtime = new ManagedContractRuntime();
        runtime.Execute(tokenCode, ApproveCall(VaultAddr, DepositAmount), Context(TokenAddr, HolderAddr));
        runtime.Execute(vaultCode, DepositCall(DepositAmount), Context(VaultAddr, HolderAddr));

        ContractCallResult vaultBalance = runtime.Execute(
            tokenCode, BalanceOfCall(VaultAddr), Context(TokenAddr, HolderAddr));
        vaultBalance.Success.Should().BeTrue(vaultBalance.ErrorMessage);
        new BasaltReader(vaultBalance.ReturnData!).ReadUInt256().Should().Be(DepositAmount);

        ContractCallResult holderBalance = runtime.Execute(
            tokenCode, BalanceOfCall(HolderAddr), Context(TokenAddr, HolderAddr));
        new BasaltReader(holderBalance.ReturnData!).ReadUInt256().Should().Be(Supply - DepositAmount);
    }

    /// <summary>
    /// Calling an address with no contract at it has to fail loudly. Returning an empty result would
    /// decode to zero, false, or an empty string, and a caller believing a zero balance or a false
    /// permission check reads as a successful call that quietly meant nothing.
    /// </summary>
    [Fact]
    public void Calling_an_address_with_no_code_reverts_rather_than_returning_nothing()
    {
        byte[] vaultCode = Deploy(VaultAddr, HolderAddr, 0x0006, VaultArgs(TokenAddr));

        // The token was never deployed, so the vault's asset address holds no code.
        ContractCallResult deposit = new ManagedContractRuntime()
            .Execute(vaultCode, DepositCall(DepositAmount), Context(VaultAddr, HolderAddr));

        deposit.Success.Should().BeFalse();
        deposit.ErrorMessage.Should().Contain("no code");
    }
}
