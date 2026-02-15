using BenchmarkDotNet.Attributes;
using Basalt.Crypto;
using Basalt.Core;

namespace Basalt.Benchmarks;

/// <summary>
/// Benchmarks for cryptographic operations: BLAKE3, Ed25519, Keccak-256.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class CryptoBenchmarks
{
    private byte[] _data32 = null!;
    private byte[] _data256 = null!;
    private byte[] _data1K = null!;
    private byte[] _data64K = null!;
    private byte[] _privateKey = null!;
    private PublicKey _publicKey;
    private Signature _signature;
    private byte[] _message = null!;

    [GlobalSetup]
    public void Setup()
    {
        _data32 = new byte[32];
        _data256 = new byte[256];
        _data1K = new byte[1024];
        _data64K = new byte[65536];
        Random.Shared.NextBytes(_data32);
        Random.Shared.NextBytes(_data256);
        Random.Shared.NextBytes(_data1K);
        Random.Shared.NextBytes(_data64K);

        (_privateKey, _publicKey) = Ed25519Signer.GenerateKeyPair();
        _message = new byte[128];
        Random.Shared.NextBytes(_message);
        _signature = Ed25519Signer.Sign(_privateKey, _message);
    }

    // --- BLAKE3 ---

    [Benchmark]
    public Hash256 Blake3_32B() => Blake3Hasher.Hash(_data32);

    [Benchmark]
    public Hash256 Blake3_256B() => Blake3Hasher.Hash(_data256);

    [Benchmark]
    public Hash256 Blake3_1KB() => Blake3Hasher.Hash(_data1K);

    [Benchmark]
    public Hash256 Blake3_64KB() => Blake3Hasher.Hash(_data64K);

    [Benchmark]
    public Hash256 Blake3_HashPair() => Blake3Hasher.HashPair(Hash256.Zero, Hash256.Zero);

    // --- Keccak-256 ---

    [Benchmark]
    public byte[] Keccak256_32B() => KeccakHasher.Hash(_data32);

    [Benchmark]
    public byte[] Keccak256_256B() => KeccakHasher.Hash(_data256);

    // --- Ed25519 ---

    [Benchmark]
    public Signature Ed25519_Sign() => Ed25519Signer.Sign(_privateKey, _message);

    [Benchmark]
    public bool Ed25519_Verify() => Ed25519Signer.Verify(_publicKey, _message, _signature);

    [Benchmark]
    public (byte[], PublicKey) Ed25519_KeyGen() => Ed25519Signer.GenerateKeyPair();

    [Benchmark]
    public Address Ed25519_DeriveAddress() => Ed25519Signer.DeriveAddress(_publicKey);
}
