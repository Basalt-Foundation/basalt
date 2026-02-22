using Basalt.Api.GraphQL;
using Basalt.Api.Rest;
using Basalt.Core;
using Basalt.Execution;
using Basalt.Storage;
using FluentAssertions;
using Xunit;

namespace Basalt.Api.Tests;

/// <summary>
/// Tests covering specific audit findings for the API layer (Issue #4).
/// </summary>
public class AuditRemediationTests
{
    private static readonly ChainParameters TestChainParams = new()
    {
        ChainId = 31337,
        NetworkName = "test",
        BlockGasLimit = 100_000_000,
    };

    private static Address MakeAddress(byte seed)
    {
        var bytes = new byte[Address.Size];
        bytes[0] = seed;
        return new Address(bytes);
    }

    private static Hash256 MakeHash(byte seed)
    {
        var bytes = new byte[Hash256.Size];
        bytes[0] = seed;
        return new Hash256(bytes);
    }

    private static Block AddGenesisBlock(ChainManager chainManager)
    {
        var header = new BlockHeader
        {
            Number = 0,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = 1_000_000,
            Proposer = Address.Zero,
            ChainId = TestChainParams.ChainId,
            GasUsed = 0,
            GasLimit = TestChainParams.BlockGasLimit,
        };
        var genesis = new Block { Header = header, Transactions = [] };
        chainManager.AddBlock(genesis);
        return genesis;
    }

    private static Block AddBlock(ChainManager chainManager, ulong number)
    {
        var parent = chainManager.LatestBlock!;
        var header = new BlockHeader
        {
            Number = number,
            ParentHash = parent.Hash,
            StateRoot = MakeHash((byte)(number & 0xFF)),
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = 1_000_000 + (long)(number * 400),
            Proposer = MakeAddress(0x01),
            ChainId = TestChainParams.ChainId,
            GasUsed = 1_000 * number,
            GasLimit = TestChainParams.BlockGasLimit,
        };
        var block = new Block { Header = header, Transactions = [] };
        chainManager.AddBlock(block);
        return block;
    }

    // ── H-2: gRPC signature/pubkey length validation ───────────────────

    [Fact]
    public void TransactionRequest_ToTransaction_Rejects_Short_Signature()
    {
        var request = new TransactionRequest
        {
            Type = 0,
            Nonce = 0,
            Sender = "0x" + new string('0', 40),
            To = "0x" + new string('0', 40),
            Value = "0",
            GasLimit = 21_000,
            GasPrice = "1",
            ChainId = 1,
            Signature = "0x" + new string('0', 64), // 32 bytes, should be 64
            SenderPublicKey = "0x" + new string('0', 64),
        };

        var act = () => request.ToTransaction();
        act.Should().Throw<ArgumentException>().WithMessage("*64 bytes*");
    }

    [Fact]
    public void TransactionRequest_ToTransaction_Rejects_Short_PublicKey()
    {
        var request = new TransactionRequest
        {
            Type = 0,
            Nonce = 0,
            Sender = "0x" + new string('0', 40),
            To = "0x" + new string('0', 40),
            Value = "0",
            GasLimit = 21_000,
            GasPrice = "1",
            ChainId = 1,
            Signature = "0x" + new string('0', 128),
            SenderPublicKey = "0x" + new string('0', 32), // 16 bytes, should be 32
        };

        var act = () => request.ToTransaction();
        act.Should().Throw<ArgumentException>().WithMessage("*32 bytes*");
    }

    [Fact]
    public void TransactionRequest_ToTransaction_Accepts_Valid_Lengths()
    {
        var request = new TransactionRequest
        {
            Type = 0,
            Nonce = 0,
            Sender = "0x" + new string('a', 40),
            To = "0x" + new string('b', 40),
            Value = "0",
            GasLimit = 21_000,
            GasPrice = "1",
            ChainId = 1,
            Signature = "0x" + new string('c', 128), // 64 bytes
            SenderPublicKey = "0x" + new string('d', 64), // 32 bytes
        };

        var tx = request.ToTransaction();
        tx.Should().NotBeNull();
    }

    // ── M-1: Faucet rate limit 0x prefix bypass ────────────────────────

    [Fact]
    public void FaucetStatusResponse_HasCorrectProperties()
    {
        var response = new FaucetStatusResponse
        {
            FaucetAddress = "ABC123",
            Available = true,
            Balance = "500000000000000000000000000",
            Nonce = 5,
            PendingNonce = 7,
            CooldownSeconds = 60,
        };

        response.FaucetAddress.Should().Be("ABC123");
        response.Available.Should().BeTrue();
        response.Balance.Should().Be("500000000000000000000000000");
        response.Nonce.Should().Be(5);
        response.PendingNonce.Should().Be(7);
        response.CooldownSeconds.Should().Be(60);
    }

    // ── M-6: GetBlocks includes genesis ────────────────────────────────

    [Fact]
    public void GetBlocks_IncludesGenesis()
    {
        var chainManager = new ChainManager();
        AddGenesisBlock(chainManager);
        AddBlock(chainManager, 1);
        var query = new Query();

        var results = query.GetBlocks(100, chainManager);

        // M-6: Should include genesis (block 0)
        results.Should().HaveCount(2);
        results[0].Number.Should().Be(1);
        results[1].Number.Should().Be(0);
    }

    [Fact]
    public void GetBlocks_OnlyGenesis_ReturnsGenesis()
    {
        var chainManager = new ChainManager();
        AddGenesisBlock(chainManager);
        var query = new Query();

        var results = query.GetBlocks(10, chainManager);

        // M-6: Genesis-only chain should return 1 block
        results.Should().HaveCount(1);
        results[0].Number.Should().Be(0);
    }

    // ── L-4: StripHexPrefix consistent handling ────────────────────────

    [Fact]
    public void TransactionRequest_ToTransaction_Handles_0x_Prefix_On_Data()
    {
        var request = new TransactionRequest
        {
            Type = 0,
            Nonce = 0,
            Sender = "0x" + new string('0', 40),
            To = "0x" + new string('0', 40),
            Value = "0",
            GasLimit = 21_000,
            GasPrice = "1",
            Data = "0xCAFE",
            ChainId = 1,
            Signature = "0x" + new string('0', 128),
            SenderPublicKey = "0x" + new string('0', 64),
        };

        var tx = request.ToTransaction();
        tx.Data.Should().BeEquivalentTo(new byte[] { 0xCA, 0xFE });
    }

    [Fact]
    public void TransactionRequest_ToTransaction_Handles_No_0x_Prefix_On_Data()
    {
        var request = new TransactionRequest
        {
            Type = 0,
            Nonce = 0,
            Sender = "0x" + new string('0', 40),
            To = "0x" + new string('0', 40),
            Value = "0",
            GasLimit = 21_000,
            GasPrice = "1",
            Data = "CAFE",
            ChainId = 1,
            Signature = "0x" + new string('0', 128),
            SenderPublicKey = "0x" + new string('0', 64),
        };

        var tx = request.ToTransaction();
        tx.Data.Should().BeEquivalentTo(new byte[] { 0xCA, 0xFE });
    }

    // ── M-2: BlockResponse includes BaseFee ────────────────────────────

    [Fact]
    public void BlockResponse_FromBlock_IncludesBaseFee()
    {
        var header = new BlockHeader
        {
            Number = 1,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = 1_000_000,
            Proposer = Address.Zero,
            ChainId = 1,
            GasUsed = 0,
            GasLimit = 100_000_000,
            BaseFee = new UInt256(1000),
        };
        var block = new Block { Header = header, Transactions = [] };

        var response = BlockResponse.FromBlock(block);

        // M-2: BaseFee must be mapped
        response.BaseFee.Should().Be("1000");
    }

    // ── L-6: GraphQL receipt deduplication ──────────────────────────────

    [Fact]
    public void GetReceipt_ReturnsNull_For_Invalid_Hash()
    {
        var chainManager = new ChainManager();
        var query = new Query();
        var services = new TestServiceProvider();

        var result = query.GetReceipt("not-a-hash", chainManager, services);
        result.Should().BeNull();
    }

    [Fact]
    public void GetTransaction_ReturnsNull_For_Invalid_Hash()
    {
        var chainManager = new ChainManager();
        var query = new Query();
        var services = new TestServiceProvider();

        var result = query.GetTransaction("zzz", chainManager, services);
        result.Should().BeNull();
    }

    // ── GraphQL BlockResult includes BaseFee ────────────────────────────

    [Fact]
    public void BlockResult_FromBlock_IncludesBaseFee()
    {
        var header = new BlockHeader
        {
            Number = 5,
            ParentHash = Hash256.Zero,
            StateRoot = Hash256.Zero,
            TransactionsRoot = Hash256.Zero,
            ReceiptsRoot = Hash256.Zero,
            Timestamp = 1_000_000,
            Proposer = Address.Zero,
            ChainId = 1,
            GasUsed = 0,
            GasLimit = 100_000_000,
            BaseFee = new UInt256(42),
        };
        var block = new Block { Header = header, Transactions = [] };

        var result = BlockResult.FromBlock(block);
        result.BaseFee.Should().Be("42");
    }

    // ── NEW-7: GraphQL mutation error does NOT leak exception type ──────

    [Fact]
    public void SubmitTransaction_InvalidInput_ReturnsGenericError()
    {
        var chainManager = new ChainManager();
        var mempool = new Mempool(100);
        var validator = new TransactionValidator(TestChainParams);
        var stateDb = new InMemoryStateDb();
        var mutation = new Mutation();

        var input = new TransactionInput
        {
            Type = 0,
            Sender = "invalid",
            To = "invalid",
            Value = "0",
            GasLimit = 21_000,
            GasPrice = "1",
            ChainId = 1,
            Signature = "bad",
            SenderPublicKey = "bad",
        };

        var result = mutation.SubmitTransaction(input, chainManager, mempool, validator, stateDb);
        result.Success.Should().BeFalse();
        // NEW-7: Error message must NOT leak exception type name
        result.ErrorMessage.Should().Be("Transaction submission failed");
        result.ErrorMessage.Should().NotContain("Exception");
    }

    // ── M-3: TransactionInput EIP-1559 fields ──────────────────────────

    [Fact]
    public void TransactionInput_ToTransaction_MapsEip1559Fields()
    {
        var input = new TransactionInput
        {
            Type = 0,
            Nonce = 0,
            Sender = "0x" + new string('a', 40),
            To = "0x" + new string('b', 40),
            Value = "0",
            GasLimit = 21_000,
            GasPrice = "1",
            MaxFeePerGas = "100",
            MaxPriorityFeePerGas = "10",
            ChainId = 1,
            Signature = "0x" + new string('0', 128),
            SenderPublicKey = "0x" + new string('0', 64),
        };

        var tx = input.ToTransaction();
        tx.MaxFeePerGas.Should().Be(UInt256.Parse("100"));
        tx.MaxPriorityFeePerGas.Should().Be(UInt256.Parse("10"));
    }

    [Fact]
    public void TransactionInput_ToTransaction_NullEip1559Fields_DefaultToZero()
    {
        var input = new TransactionInput
        {
            Type = 0,
            Nonce = 0,
            Sender = "0x" + new string('a', 40),
            To = "0x" + new string('b', 40),
            Value = "0",
            GasLimit = 21_000,
            GasPrice = "1",
            MaxFeePerGas = null,
            MaxPriorityFeePerGas = null,
            ChainId = 1,
            Signature = "0x" + new string('0', 128),
            SenderPublicKey = "0x" + new string('0', 64),
        };

        var tx = input.ToTransaction();
        tx.MaxFeePerGas.Should().Be(UInt256.Zero);
        tx.MaxPriorityFeePerGas.Should().Be(UInt256.Zero);
    }

    /// <summary>Minimal IServiceProvider for GraphQL Query tests.</summary>
    private sealed class TestServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
