using Basalt.Core;
using Basalt.Crypto;
using FluentAssertions;
using Xunit;

namespace Basalt.Execution.Tests;

/// <summary>
/// Pins the exact signing payload and Ed25519 signature of a ContractCall transaction with fixed inputs.
/// Trilith.AnchorCli reimplements this signing over the light Basalt packages (Core/Codec/Crypto) without
/// referencing Basalt.Execution, and cross-checks against these same golden bytes. If Basalt's transaction
/// signing format ever changes, this test breaks here and the AnchorCli cross-check breaks there, so the two
/// can never drift apart silently.
/// </summary>
public class AnchorTxSigningGoldenTests
{
    // Fixed private key 0x01..0x20.
    internal static byte[] PrivateKey()
    {
        var k = new byte[32];
        for (int i = 0; i < 32; i++) k[i] = (byte)(i + 1);
        return k;
    }

    // Fixed transaction fields (a TrilithAnchor-shaped ContractCall to system address 0x100A).
    internal const ulong Nonce = 7;
    internal const ulong GasLimit = 1_000_000;
    internal const uint ChainId = 4242;
    internal static Address To() { var a = new byte[20]; a[18] = 0x10; a[19] = 0x0A; return new Address(a); }
    internal static byte[] Data() { var d = new byte[10]; for (int i = 0; i < 10; i++) d[i] = (byte)(i + 1); return d; }

    internal const string GoldenPub = "79b5562e8fe654f94078b112e8a98ba7901f853ae695bed7e0e3910bad049664";
    internal const string GoldenSender = "fa5be9b63e2d3ca99a055be511cc9742ad5e0496";
    internal const string GoldenPayload =
        "020700000000000000fa5be9b63e2d3ca99a055be511cc9742ad5e0496000000000000000000000000000000000000100a000000000000000000000000000000000000000000000000000000000000000040420f00000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000a0102030405060708090a00921000000000000000000000000000000000000000000000000000000000000000000000";
    internal const string GoldenSignature =
        "255b18757fff3887852fc73c3fe09c4cd201dba8f6d1c983ca1ee78d650fba9f5ca90bee1590f783d1c11aa02f80a2c65d0ef1580fc5a1aac6afd5433ec7c60c";

    [Fact]
    public void ContractCall_signing_matches_the_golden()
    {
        var priv = PrivateKey();
        var pub = Ed25519Signer.GetPublicKey(priv);
        var sender = Ed25519Signer.DeriveAddress(pub);

        Convert.ToHexStringLower(pub.ToArray()).Should().Be(GoldenPub);
        sender.ToHexString().Should().Be("0x" + GoldenSender);

        var unsigned = new Transaction
        {
            Type = TransactionType.ContractCall,
            Nonce = Nonce,
            Sender = sender,
            To = To(),
            Value = UInt256.Zero,
            GasLimit = GasLimit,
            GasPrice = new UInt256(1),
            ChainId = ChainId,
            Data = Data(),
        };

        var buf = new byte[unsigned.GetSigningPayloadSize()];
        var payload = unsigned.WriteSigningPayload(buf).ToArray();
        var signed = Transaction.Sign(unsigned, priv);

        Convert.ToHexStringLower(payload).Should().Be(GoldenPayload);
        Convert.ToHexStringLower(signed.Signature.ToArray()).Should().Be(GoldenSignature);
    }
}
