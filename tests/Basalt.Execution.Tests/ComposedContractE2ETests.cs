using Basalt.Codec;
using Basalt.Core;
using Basalt.Crypto;
using Basalt.Execution.VM;
using Basalt.Sdk.Contracts;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

/// <summary>
/// A composed contract driven the way a validator drives one: signed transactions through
/// <see cref="TransactionExecutor"/>, not the runtime called directly.
///
/// This is the path that had never been exercised. Cross-contract calls reverted on a real chain while
/// passing every unit test, which meant transfer policies, the feature the chain is built around, could
/// not run at all. Nothing said so, because the tests that covered policies called the contracts as C#
/// objects.
/// </summary>
public class ComposedContractE2ETests
{
    private readonly ChainParameters _chainParams = ChainParameters.Devnet;

    private (byte[] Key, Address Address) NewAccount()
    {
        var (privateKey, publicKey) = Ed25519Signer.GenerateKeyPair();
        return (privateKey, Ed25519Signer.DeriveAddress(publicKey));
    }

    private static InMemoryStateDb FundedState(params Address[] accounts)
    {
        var stateDb = new InMemoryStateDb();
        foreach (var address in accounts)
        {
            stateDb.SetAccount(address, new AccountState
            {
                Nonce = 0,
                Balance = (UInt256)1_000_000_000_000UL,
                StorageRoot = Hash256.Zero,
                CodeHash = Hash256.Zero,
                AccountType = AccountType.ExternallyOwned,
                ComplianceHash = Hash256.Zero,
            });
        }

        return stateDb;
    }

    private BlockHeader Header(InMemoryStateDb stateDb) => new()
    {
        Number = 1,
        ParentHash = Hash256.Zero,
        StateRoot = stateDb.ComputeStateRoot(),
        TransactionsRoot = Hash256.Zero,
        ReceiptsRoot = Hash256.Zero,
        Timestamp = 1_700_000_000_000,
        Proposer = Address.Zero,
        ChainId = _chainParams.ChainId,
        GasUsed = 0,
        GasLimit = _chainParams.BlockGasLimit,
        BaseFee = _chainParams.InitialBaseFee,
    };

    private Transaction Signed(
        byte[] key, Address sender, TransactionType type, Address to, byte[] data, ulong nonce)
        => Transaction.Sign(new Transaction
        {
            Type = type,
            Nonce = nonce,
            Sender = sender,
            To = to,
            Value = UInt256.Zero,
            GasLimit = 20_000_000,
            GasPrice = _chainParams.InitialBaseFee,
            Data = data,
            ChainId = _chainParams.ChainId,
        }, key);

    private static byte[] TokenManifest(UInt256 supply)
    {
        var buffer = new byte[256];
        var writer = new BasaltWriter(buffer);
        writer.WriteString("Regulated");
        writer.WriteString("REG");
        writer.WriteByte(18);
        writer.WriteUInt256(supply);
        return ContractRegistry.BuildManifest(0x0001, buffer[..writer.Position]);
    }


    private static byte[] TransferCall(Address to, UInt256 amount)
    {
        var buffer = new byte[256];
        var writer = new BasaltWriter(buffer);
        writer.WriteBytes(to.ToArray());
        writer.WriteUInt256(amount);
        return WithSelector("Transfer", buffer, writer.Position);
    }

    private static byte[] BalanceOfCall(Address account)
    {
        var buffer = new byte[64];
        var writer = new BasaltWriter(buffer);
        writer.WriteBytes(account.ToArray());
        return WithSelector("BalanceOf", buffer, writer.Position);
    }

    private static byte[] WithSelector(string method, byte[] args, int length)
    {
        var selector = SelectorHelper.ComputeSelectorBytes(method);
        var callData = new byte[selector.Length + length];
        selector.CopyTo(callData, 0);
        args.AsSpan(0, length).CopyTo(callData.AsSpan(selector.Length));
        return callData;
    }

    /// <summary>
    /// Deploy a token, move some of it, and read the balance back. The whole trip, signed, metered, and
    /// committed, which is the only version of "it works" that counts.
    /// </summary>
    [Fact]
    public void A_token_deploys_and_transfers_through_signed_transactions()
    {
        var (holderKey, holder) = NewAccount();
        var (_, recipient) = NewAccount();

        var stateDb = FundedState(holder);
        var executor = new TransactionExecutor(_chainParams);
        var header = Header(stateDb);

        TransactionReceipt deploy = executor.Execute(
            Signed(holderKey, holder, TransactionType.ContractDeploy, Address.Zero, TokenManifest(new UInt256(1_000_000)), 0),
            stateDb, header, 0);
        deploy.Success.Should().BeTrue("deploying a standard token has to work before anything else does");

        Address token = deploy.To;

        TransactionReceipt transfer = executor.Execute(
            Signed(holderKey, holder, TransactionType.ContractCall, token, TransferCall(recipient, new UInt256(4_242)), 1),
            stateDb, header, 1);
        transfer.Success.Should().BeTrue(transfer.ErrorCode.ToString());

        // Read the balance back out of committed state rather than trusting the receipt.
        var code = stateDb.GetStorage(token, CodeKey())!;
        var view = new VmExecutionContext
        {
            Caller = holder,
            ContractAddress = token,
            Value = UInt256.Zero,
            BlockTimestamp = (ulong)header.Timestamp,
            BlockNumber = header.Number,
            BlockProposer = header.Proposer,
            ChainId = header.ChainId,
            GasMeter = new GasMeter(20_000_000),
            StateDb = stateDb,
            CallDepth = 0,
        };

        ContractCallResult balance = new ManagedContractRuntime()
            .Execute(code, BalanceOfCall(recipient), view);
        new BasaltReader(balance.ReturnData!).ReadUInt256().Should().Be(new UInt256(4_242));
    }

    /// <summary>
    /// The receipt has to carry what happened. An indexer, an explorer, and anyone auditing a transfer
    /// read this and nothing else.
    /// </summary>
    [Fact]
    public void The_receipt_of_a_real_transaction_carries_the_event_fields()
    {
        var (holderKey, holder) = NewAccount();
        var (_, recipient) = NewAccount();

        var stateDb = FundedState(holder);
        var executor = new TransactionExecutor(_chainParams);
        var header = Header(stateDb);

        TransactionReceipt deploy = executor.Execute(
            Signed(holderKey, holder, TransactionType.ContractDeploy, Address.Zero, TokenManifest(new UInt256(1_000_000)), 0),
            stateDb, header, 0);

        TransactionReceipt transfer = executor.Execute(
            Signed(holderKey, holder, TransactionType.ContractCall, deploy.To, TransferCall(recipient, new UInt256(4_242)), 1),
            stateDb, header, 1);

        transfer.Success.Should().BeTrue(transfer.ErrorCode.ToString());
        transfer.Logs.Should().NotBeEmpty("a transfer emits an event");

        var reader = new BasaltReader(transfer.Logs[0].Data!);
        reader.ReadBytes().ToArray().Should().Equal(holder.ToArray());
        reader.ReadBytes().ToArray().Should().Equal(recipient.ToArray());
        reader.ReadUInt256().Should().Be(new UInt256(4_242));
    }

    private static Hash256 CodeKey()
    {
        var key = new byte[32];
        key[0] = 0xFF;
        key[1] = 0x01;
        return new Hash256(key);
    }

    private static byte[] AddressArgCall(string method, Address account)
    {
        var buffer = new byte[64];
        var writer = new BasaltWriter(buffer);
        writer.WriteBytes(account.ToArray());
        return WithSelector(method, buffer, writer.Position);
    }

    /// <summary>
    /// The feature the chain is named for, run the way it would actually run.
    ///
    /// A sanctions policy is deployed, attached to a token, and the token is asked to move value to a
    /// sanctioned account. The refusal has to come from the policy contract, reached by a cross-contract
    /// call from the token, which is exactly the call that used to revert on a validator while every
    /// unit test covering policies passed.
    /// </summary>
    [Fact]
    public void A_sanctions_policy_actually_blocks_a_transfer()
    {
        var (adminKey, admin) = NewAccount();
        var (_, blocked) = NewAccount();
        var (_, allowed) = NewAccount();

        var stateDb = FundedState(admin);
        var executor = new TransactionExecutor(_chainParams);
        var header = Header(stateDb);
        ulong nonce = 0;

        TransactionReceipt token = executor.Execute(
            Signed(adminKey, admin, TransactionType.ContractDeploy, Address.Zero,
                TokenManifest(new UInt256(1_000_000)), nonce++), stateDb, header, 0);
        token.Success.Should().BeTrue();

        TransactionReceipt policy = executor.Execute(
            Signed(adminKey, admin, TransactionType.ContractDeploy, Address.Zero,
                ContractRegistry.BuildManifest(0x000B, []), nonce++), stateDb, header, 1);
        policy.Success.Should().BeTrue("the sanctions policy has to deploy");

        TransactionReceipt sanction = executor.Execute(
            Signed(adminKey, admin, TransactionType.ContractCall, policy.To,
                AddressArgCall("AddSanction", blocked), nonce++), stateDb, header, 2);
        sanction.Success.Should().BeTrue(sanction.ErrorCode.ToString());

        TransactionReceipt attach = executor.Execute(
            Signed(adminKey, admin, TransactionType.ContractCall, token.To,
                AddressArgCall("AddPolicy", policy.To), nonce++), stateDb, header, 3);
        attach.Success.Should().BeTrue(attach.ErrorCode.ToString());

        // An unsanctioned recipient still goes through, so the refusal below is the policy deciding and
        // not the policy call failing for every recipient alike.
        TransactionReceipt ok = executor.Execute(
            Signed(adminKey, admin, TransactionType.ContractCall, token.To,
                TransferCall(allowed, new UInt256(10)), nonce++), stateDb, header, 4);
        ok.Success.Should().BeTrue(ok.ErrorCode.ToString());

        TransactionReceipt refused = executor.Execute(
            Signed(adminKey, admin, TransactionType.ContractCall, token.To,
                TransferCall(blocked, new UInt256(10)), nonce++), stateDb, header, 5);

        refused.Success.Should().BeFalse("the policy refuses a transfer to a sanctioned account");
    }
}
