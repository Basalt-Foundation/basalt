using Basalt.Core;

namespace Basalt.Compliance.Tests;

/// <summary>
/// The golden compliance-circuit vector shared by the circuit tests and the COMPL-C03 enforcement tests.
/// From <c>tools/basalt-prove membership</c>: gnark v0.15, depth-4 issuer tree, credential commitment
/// MiMC(secret=42, expiry=1893456000, tier=3) at leaf index 5, nullifier MiMC(42, 7), and a revocation
/// non-membership proof against an empty revocation tree. All five public inputs are bound:
/// issuerRoot (membership), expiry and tier (in the leaf), nullifier, and revocationRoot (non-membership).
/// Tampering any of them fails verification.
/// </summary>
internal static class MembershipVector
{
    public const string VkHex =
        "aca15121d5d87f56dacc31922957e9b7476c22fd1165982d7e9a5227cd5fdef27d707b0494b18e87330d1cf3a5dc2c73" +
        "90233090d41b03b17bf1b52c41cac265e60aef1ab64eadd68be06f8b60110c3fceaab3181b991cf4c206cfd10389ed02" +
        "16838f9eac84596d1980c6bb3c131e1957c854f02db42422919d8ddac5d88350f001ccdbf4bbb8113b29c4edafbd689c" +
        "87cf388602b24e0cf5c22b43b2a5f6d0dfd9f22a50c20ae0d5e538cdf09b9c60d6361d38c9f75f8953b592b6ab85af66" +
        "0d547cba48ddaf855e31119b0a31048fa5814e1d82184c585b40e30e344e203313ef612e234c2df56ffb7c872cbd35f8" +
        "94df8deae6fa21d0cc93d62357990fa80dbc685cda753d157fc23fe8f84adf92c2427009b577e72f0dc66989e45ef93d" +
        "08e653fb139ab84b6d68cbbb8a9aec1ce490c212259a613f9268ebca2a8b2106b200d1a3be551cb9b5fc046b4533dbdd" +
        "06000000a954a9faa9ed51adaf747aafeed2867bc03a0cb06df59e1ca04a263eddf39ab33115465debb5f9944eb41617" +
        "9cfa323d8eb5d8b969b3135f9ded68e1be6705db60a407068cd51ae642f45d9b3b776d0f1c81ec03705ec1ab0a4f352b" +
        "ee6c96cb8876eb3f6f6b65b84b380c99d98f54b2364cfa99f7b7e248dd24b07d37c6648901366f48dcfb5f0b710d6506" +
        "e4b4437b8e802ecd813fd250ea0562a879ab84f5e5d5be1456c41428be204a5b139aaba60cfc520d865cfaaf8cbe6f0d" +
        "cca93c6cb93404e88e1c3b5e63227f259958de91323a236971e75aed1d18cdfa07f67b0fdb02743294b2c943eb29b709" +
        "999c7945ae0232bc2b4ec453a4c8a94c24ff57db0ceab12bc8835b43e229412ad46b999204629a0fa3aa49004d326f00" +
        "00814f55";

    public const string ProofHex =
        "a0705cf0244fe50c28000c7ad5b53337e53b0c67350b2f5c19c80a6428368b00284c51d137dc5b4ccf67f8c029318d47" +
        "80c34ab560b1ad614b3e9dad6d152f07a64e1bc429c8f25d1223b4e06d01ae42fb0561ce140d12af55da821585e33adf" +
        "1347886871c9c8cac74d45ad8a1fbc80aaac2c51291052261da694392f8856cf4ae5d436e9832d282f9408e51d1aad9f" +
        "98fa05d0f59dbae246d61f15e21a4273f109e2789898bed1e3edaca07ac099b8420f2566a286838147f89d5be0a5ce3e";

    // Frozen layout order: issuerRoot, nullifier, expiry (1893456000), tier (3), revocationRoot (empty tree).
    public static readonly string[] PublicInputHex =
    {
        "17e1ee6c2f7d633c138faaabc0eb014f127bc2c8c5290929e293034c590c07ab",
        "706e7b7578eccdb3df4775460af7243b71d2a3168763c75c4347f42a863c1749",
        "0000000000000000000000000000000000000000000000000000000070dbd880",
        "0000000000000000000000000000000000000000000000000000000000000003",
        "5cf842758b8b17627ab0e9144945946eb38940bb715085ac0e71754dacd72018",
    };

    public const ulong ExpirySeconds = 1893456000; // 2030-01-01
    public const ulong Tier = 3;

    public static byte[] VkBytes() => Convert.FromHexString(VkHex);
    public static byte[] ProofBytes() => Convert.FromHexString(ProofHex);

    public static byte[][] PublicInputs()
    {
        var r = new byte[PublicInputHex.Length][];
        for (int i = 0; i < r.Length; i++)
            r[i] = Convert.FromHexString(PublicInputHex[i]);
        return r;
    }

    /// <summary>The five public inputs concatenated, as a transaction carries them.</summary>
    public static byte[] FlatPublicInputs()
    {
        var flat = new byte[PublicInputHex.Length * 32];
        var pis = PublicInputs();
        for (int i = 0; i < pis.Length; i++)
            Array.Copy(pis[i], 0, flat, i * 32, 32);
        return flat;
    }

    /// <summary>The nullifier the circuit proved (public input index 1), as a Hash256.</summary>
    public static Hash256 CircuitNullifier() => new(PublicInputs()[CircuitV1Layout.Nullifier]);
}
