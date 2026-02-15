using BenchmarkDotNet.Attributes;
using Basalt.Codec;
using Basalt.Core;

namespace Basalt.Benchmarks;

/// <summary>
/// Benchmarks for binary codec operations: BasaltWriter/Reader, varint encoding.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class CodecBenchmarks
{
    private byte[] _buffer = null!;
    private Hash256 _hash;
    private Address _address;
    private UInt256 _uint256;

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new byte[4096];
        var hashBytes = new byte[32];
        Random.Shared.NextBytes(hashBytes);
        _hash = new Hash256(hashBytes);

        var addrBytes = new byte[20];
        Random.Shared.NextBytes(addrBytes);
        _address = new Address(addrBytes);

        _uint256 = UInt256.Parse("123456789012345678901234567890");
    }

    [Benchmark]
    public int WriteUInt64()
    {
        var writer = new BasaltWriter(_buffer);
        for (int i = 0; i < 100; i++)
            writer.WriteUInt64((ulong)i);
        return writer.Position;
    }

    [Benchmark]
    public int WriteVarInt()
    {
        var writer = new BasaltWriter(_buffer);
        for (int i = 0; i < 100; i++)
            writer.WriteVarInt((ulong)i * 1000);
        return writer.Position;
    }

    [Benchmark]
    public int WriteHash256()
    {
        var writer = new BasaltWriter(_buffer);
        for (int i = 0; i < 10; i++)
            writer.WriteHash256(_hash);
        return writer.Position;
    }

    [Benchmark]
    public int WriteAddress()
    {
        var writer = new BasaltWriter(_buffer);
        for (int i = 0; i < 10; i++)
            writer.WriteAddress(_address);
        return writer.Position;
    }

    [Benchmark]
    public int WriteUInt256()
    {
        var writer = new BasaltWriter(_buffer);
        for (int i = 0; i < 10; i++)
            writer.WriteUInt256(_uint256);
        return writer.Position;
    }

    [Benchmark]
    public ulong ReadUInt64()
    {
        // Write first, then read
        var writer = new BasaltWriter(_buffer);
        for (int i = 0; i < 100; i++)
            writer.WriteUInt64((ulong)i);

        var reader = new BasaltReader(_buffer.AsSpan(0, writer.Position));
        ulong sum = 0;
        for (int i = 0; i < 100; i++)
            sum += reader.ReadUInt64();
        return sum;
    }

    [Benchmark]
    public int WriteString()
    {
        var writer = new BasaltWriter(_buffer);
        writer.WriteString("Hello, Basalt Blockchain!");
        return writer.Position;
    }

    [Benchmark]
    public int WriteBlockHeaderPayload()
    {
        var writer = new BasaltWriter(_buffer);
        writer.WriteUInt64(12345);          // Number
        writer.WriteHash256(_hash);          // ParentHash
        writer.WriteHash256(_hash);          // StateRoot
        writer.WriteHash256(_hash);          // TxRoot
        writer.WriteHash256(_hash);          // ReceiptsRoot
        writer.WriteInt64(1700000000);       // Timestamp
        writer.WriteAddress(_address);       // Proposer
        writer.WriteUInt32(31337);           // ChainId
        writer.WriteUInt64(1000000);         // GasUsed
        writer.WriteUInt64(100000000);       // GasLimit
        writer.WriteUInt32(1);               // ProtocolVersion
        writer.WriteBytes([]);               // ExtraData
        return writer.Position;
    }
}
