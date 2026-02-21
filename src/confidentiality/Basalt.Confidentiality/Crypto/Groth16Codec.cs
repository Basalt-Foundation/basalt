using System.Buffers.Binary;

namespace Basalt.Confidentiality.Crypto;

/// <summary>
/// Serialization/deserialization for <see cref="VerificationKey"/> and <see cref="Groth16Proof"/>
/// using a simple length-prefixed binary format for on-chain storage.
///
/// Format for VerificationKey:
///   [AlphaG1: 48 bytes][BetaG2: 96 bytes][GammaG2: 96 bytes][DeltaG2: 96 bytes]
///   [IC count: 4 bytes LE][IC[0]: 48 bytes][IC[1]: 48 bytes]...
///
/// Format for Groth16Proof:
///   [A: 48 bytes][B: 96 bytes][C: 48 bytes]
/// </summary>
public static class Groth16Codec
{
    /// <summary>Fixed size of a serialized Groth16 proof.</summary>
    public const int ProofSize = PairingEngine.G1CompressedSize + PairingEngine.G2CompressedSize + PairingEngine.G1CompressedSize;
    // 48 + 96 + 48 = 192

    /// <summary>Fixed portion size of a serialized verification key (before IC array).</summary>
    private const int VkFixedSize =
        PairingEngine.G1CompressedSize +   // AlphaG1
        PairingEngine.G2CompressedSize +   // BetaG2
        PairingEngine.G2CompressedSize +   // GammaG2
        PairingEngine.G2CompressedSize +   // DeltaG2
        sizeof(int);                        // IC count

    // 48 + 96 + 96 + 96 + 4 = 340

    /// <summary>
    /// Serialize a <see cref="Groth16Proof"/> to bytes.
    /// </summary>
    public static byte[] EncodeProof(Groth16Proof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);

        var result = new byte[ProofSize];
        int offset = 0;

        Buffer.BlockCopy(proof.A, 0, result, offset, PairingEngine.G1CompressedSize);
        offset += PairingEngine.G1CompressedSize;

        Buffer.BlockCopy(proof.B, 0, result, offset, PairingEngine.G2CompressedSize);
        offset += PairingEngine.G2CompressedSize;

        Buffer.BlockCopy(proof.C, 0, result, offset, PairingEngine.G1CompressedSize);

        return result;
    }

    /// <summary>
    /// Deserialize a <see cref="Groth16Proof"/> from bytes.
    /// </summary>
    public static Groth16Proof DecodeProof(ReadOnlySpan<byte> data)
    {
        if (data.Length != ProofSize)
            throw new ArgumentException($"Proof data must be exactly {ProofSize} bytes, got {data.Length}.");

        int offset = 0;

        var a = data.Slice(offset, PairingEngine.G1CompressedSize).ToArray();
        offset += PairingEngine.G1CompressedSize;

        var b = data.Slice(offset, PairingEngine.G2CompressedSize).ToArray();
        offset += PairingEngine.G2CompressedSize;

        var c = data.Slice(offset, PairingEngine.G1CompressedSize).ToArray();

        return new Groth16Proof { A = a, B = b, C = c };
    }

    /// <summary>
    /// Serialize a <see cref="VerificationKey"/> to bytes.
    /// </summary>
    public static byte[] EncodeVerificationKey(VerificationKey vk)
    {
        ArgumentNullException.ThrowIfNull(vk);

        int totalSize = VkFixedSize + vk.IC.Length * PairingEngine.G1CompressedSize;
        var result = new byte[totalSize];
        int offset = 0;

        Buffer.BlockCopy(vk.AlphaG1, 0, result, offset, PairingEngine.G1CompressedSize);
        offset += PairingEngine.G1CompressedSize;

        Buffer.BlockCopy(vk.BetaG2, 0, result, offset, PairingEngine.G2CompressedSize);
        offset += PairingEngine.G2CompressedSize;

        Buffer.BlockCopy(vk.GammaG2, 0, result, offset, PairingEngine.G2CompressedSize);
        offset += PairingEngine.G2CompressedSize;

        Buffer.BlockCopy(vk.DeltaG2, 0, result, offset, PairingEngine.G2CompressedSize);
        offset += PairingEngine.G2CompressedSize;

        // H-01: Use explicit little-endian encoding to match DecodeVerificationKey.
        // BitConverter.TryWriteBytes uses platform-native endianness which could
        // differ between big-endian and little-endian architectures.
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), vk.IC.Length);
        offset += sizeof(int);

        foreach (var ic in vk.IC)
        {
            Buffer.BlockCopy(ic, 0, result, offset, PairingEngine.G1CompressedSize);
            offset += PairingEngine.G1CompressedSize;
        }

        return result;
    }

    /// <summary>
    /// Deserialize a <see cref="VerificationKey"/> from bytes.
    /// </summary>
    public static VerificationKey DecodeVerificationKey(ReadOnlySpan<byte> data)
    {
        if (data.Length < VkFixedSize)
            throw new ArgumentException($"Verification key data too short: {data.Length} bytes, minimum {VkFixedSize}.");

        int offset = 0;

        var alphaG1 = data.Slice(offset, PairingEngine.G1CompressedSize).ToArray();
        offset += PairingEngine.G1CompressedSize;

        var betaG2 = data.Slice(offset, PairingEngine.G2CompressedSize).ToArray();
        offset += PairingEngine.G2CompressedSize;

        var gammaG2 = data.Slice(offset, PairingEngine.G2CompressedSize).ToArray();
        offset += PairingEngine.G2CompressedSize;

        var deltaG2 = data.Slice(offset, PairingEngine.G2CompressedSize).ToArray();
        offset += PairingEngine.G2CompressedSize;

        // F-12: Use explicit endianness and validate IC count bounds.
        int icCount = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, sizeof(int)));
        offset += sizeof(int);

        // L-04: Validate IC count to prevent negative values or absurd allocations.
        // Upper bound of 1024 is generous: a circuit with >1024 public inputs would
        // be impractical. This prevents DoS via crafted data causing large allocations.
        if (icCount < 0 || icCount > 1024)
            throw new ArgumentException($"Invalid IC count: {icCount}. Must be 0-1024.");

        int expectedRemaining = icCount * PairingEngine.G1CompressedSize;
        if (data.Length - offset < expectedRemaining)
            throw new ArgumentException($"Verification key data too short for {icCount} IC points.");

        var ic = new byte[icCount][];
        for (int i = 0; i < icCount; i++)
        {
            ic[i] = data.Slice(offset, PairingEngine.G1CompressedSize).ToArray();
            offset += PairingEngine.G1CompressedSize;
        }

        return new VerificationKey
        {
            AlphaG1 = alphaG1,
            BetaG2 = betaG2,
            GammaG2 = gammaG2,
            DeltaG2 = deltaG2,
            IC = ic,
        };
    }
}
