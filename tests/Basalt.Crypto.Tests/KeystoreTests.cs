using Basalt.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Crypto.Tests;

public class KeystoreTests
{
    [Fact]
    public void Decrypt_InvalidVersion_ThrowsNotSupportedException()
    {
        var ks = Keystore.Encrypt(new byte[32], "password");
        ks.Version = 2;
        var act = () => Keystore.Decrypt(ks, "password");
        act.Should().Throw<NotSupportedException>().WithMessage("*version*");
    }

    [Fact]
    public void Decrypt_ZeroIterations_ThrowsArgumentException()
    {
        var ks = Keystore.Encrypt(new byte[32], "password");
        ks.Crypto.KdfParams.Iterations = 0;
        var act = () => Keystore.Decrypt(ks, "password");
        act.Should().Throw<ArgumentException>().WithMessage("*iterations*");
    }

    [Fact]
    public void Decrypt_ZeroMemory_ThrowsArgumentException()
    {
        var ks = Keystore.Encrypt(new byte[32], "password");
        ks.Crypto.KdfParams.MemoryKB = 0;
        var act = () => Keystore.Decrypt(ks, "password");
        act.Should().Throw<ArgumentException>().WithMessage("*memory*");
    }

    [Fact]
    public void Decrypt_ZeroParallelism_ThrowsArgumentException()
    {
        var ks = Keystore.Encrypt(new byte[32], "password");
        ks.Crypto.KdfParams.Parallelism = 0;
        var act = () => Keystore.Decrypt(ks, "password");
        act.Should().Throw<ArgumentException>().WithMessage("*parallelism*");
    }
}
