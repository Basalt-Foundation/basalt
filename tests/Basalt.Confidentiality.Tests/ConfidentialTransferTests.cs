using Basalt.Confidentiality.Crypto;
using Basalt.Confidentiality.Transactions;
using Basalt.Core;
using FluentAssertions;
using Xunit;

namespace Basalt.Confidentiality.Tests;

public class ConfidentialTransferTests
{
    /// <summary>
    /// Creates a balanced single-input single-output confidential transfer
    /// where both input and output commit to the same amount.
    /// </summary>
    private static ConfidentialTransfer CreateBalancedTransfer(
        ulong amount,
        byte inputBlindingValue,
        byte outputBlindingValue)
    {
        var r1 = new byte[32];
        r1[31] = inputBlindingValue;

        var r2 = new byte[32];
        r2[31] = outputBlindingValue;

        var balanceBlinding = new byte[32];
        balanceBlinding[31] = (byte)(inputBlindingValue - outputBlindingValue);

        var inputCommitment = PedersenCommitment.Commit(new UInt256(amount), r1);
        var outputCommitment = PedersenCommitment.Commit(new UInt256(amount), r2);

        return new ConfidentialTransfer
        {
            InputCommitments = new[] { inputCommitment },
            OutputCommitments = new[] { outputCommitment },
            BalanceProofBlinding = balanceBlinding
        };
    }

    [Fact]
    public void ValidateBalance_BalancedTransfer_ReturnsTrue()
    {
        // Input and output both commit to 100, with blinding factors 5 and 3.
        // BalanceProofBlinding = 5 - 3 = 2.
        var transfer = CreateBalancedTransfer(amount: 100, inputBlindingValue: 5, outputBlindingValue: 3);

        TransferValidator.ValidateBalance(transfer).Should().BeTrue();
    }

    [Fact]
    public void ValidateBalance_UnbalancedTransfer_ReturnsFalse()
    {
        // Input commits to 100, output commits to 50 -- amounts do not balance.
        var r1 = new byte[32];
        r1[31] = 5;

        var r2 = new byte[32];
        r2[31] = 3;

        var balanceBlinding = new byte[32];
        balanceBlinding[31] = 2; // 5 - 3

        var inputCommitment = PedersenCommitment.Commit(new UInt256(100), r1);
        var outputCommitment = PedersenCommitment.Commit(new UInt256(50), r2);

        var transfer = new ConfidentialTransfer
        {
            InputCommitments = new[] { inputCommitment },
            OutputCommitments = new[] { outputCommitment },
            BalanceProofBlinding = balanceBlinding
        };

        TransferValidator.ValidateBalance(transfer).Should().BeFalse();
    }

    [Fact]
    public void ValidateBalance_WrongBlinding_ReturnsFalse()
    {
        // Amounts balance (both 100) but BalanceProofBlinding is wrong.
        var r1 = new byte[32];
        r1[31] = 5;

        var r2 = new byte[32];
        r2[31] = 3;

        // Correct blinding would be 2, but we use 99.
        var wrongBlinding = new byte[32];
        wrongBlinding[31] = 99;

        var inputCommitment = PedersenCommitment.Commit(new UInt256(100), r1);
        var outputCommitment = PedersenCommitment.Commit(new UInt256(100), r2);

        var transfer = new ConfidentialTransfer
        {
            InputCommitments = new[] { inputCommitment },
            OutputCommitments = new[] { outputCommitment },
            BalanceProofBlinding = wrongBlinding
        };

        TransferValidator.ValidateBalance(transfer).Should().BeFalse();
    }

    [Fact]
    public void ValidateBalance_MultipleInputsOutputs_ReturnsTrue()
    {
        // Two inputs, two outputs that balance:
        //   Input 1: amount=10, r1=0x0A (10)
        //   Input 2: amount=20, r2=0x14 (20)
        //   Output 1: amount=15, r3=0x0C (12)
        //   Output 2: amount=15, r4=0x08 (8)
        //   balanceBlinding = (10 + 20) - (12 + 8) = 10 (0x0A)

        var r1 = new byte[32];
        r1[31] = 0x0A;
        var r2 = new byte[32];
        r2[31] = 0x14;
        var r3 = new byte[32];
        r3[31] = 0x0C;
        var r4 = new byte[32];
        r4[31] = 0x08;

        var in1 = PedersenCommitment.Commit(new UInt256(10), r1);
        var in2 = PedersenCommitment.Commit(new UInt256(20), r2);
        var out1 = PedersenCommitment.Commit(new UInt256(15), r3);
        var out2 = PedersenCommitment.Commit(new UInt256(15), r4);

        var balanceBlinding = new byte[32];
        balanceBlinding[31] = 0x0A; // (10 + 20) - (12 + 8) = 10

        var transfer = new ConfidentialTransfer
        {
            InputCommitments = new[] { in1, in2 },
            OutputCommitments = new[] { out1, out2 },
            BalanceProofBlinding = balanceBlinding
        };

        TransferValidator.ValidateBalance(transfer).Should().BeTrue();
    }

    [Fact]
    public void ValidateBalance_NullTransfer_ReturnsFalse()
    {
        TransferValidator.ValidateBalance(null!).Should().BeFalse();
    }

    [Fact]
    public void ValidateBalance_EmptyCommitments_ReturnsFalse()
    {
        var transfer = new ConfidentialTransfer
        {
            InputCommitments = Array.Empty<byte[]>(),
            OutputCommitments = Array.Empty<byte[]>(),
            BalanceProofBlinding = new byte[32]
        };

        TransferValidator.ValidateBalance(transfer).Should().BeFalse();
    }

    [Fact]
    public void ValidateTransfer_ValidNoRangeProof_ReturnsFalse()
    {
        // F-02: A balanced transfer with no range proof should now fail ValidateTransfer
        // because range proofs are mandatory to prevent hidden inflation.
        var transfer = CreateBalancedTransfer(amount: 42, inputBlindingValue: 7, outputBlindingValue: 3);

        TransferValidator.ValidateTransfer(transfer, null).Should().BeFalse();
    }

    [Fact]
    public void ValidateRangeProof_NoProof_ReturnsFalse()
    {
        // F-02: When no range proof is present, ValidateRangeProof should return false.
        var transfer = CreateBalancedTransfer(amount: 10, inputBlindingValue: 5, outputBlindingValue: 2);

        TransferValidator.ValidateRangeProof(transfer, null).Should().BeFalse();
    }

    [Fact]
    public void ValidateBalance_TamperedCommitment_ReturnsFalse()
    {
        // Create a valid balanced transfer, then tamper with one byte of an output commitment.
        var transfer = CreateBalancedTransfer(amount: 100, inputBlindingValue: 5, outputBlindingValue: 3);

        // Tamper with the output commitment by flipping a bit in the middle.
        var tampered = (byte[])transfer.OutputCommitments[0].Clone();
        tampered[24] ^= 0x01;

        var tamperedTransfer = new ConfidentialTransfer
        {
            InputCommitments = transfer.InputCommitments,
            OutputCommitments = new[] { tampered },
            BalanceProofBlinding = transfer.BalanceProofBlinding
        };

        TransferValidator.ValidateBalance(tamperedTransfer).Should().BeFalse();
    }

    // ── Null/invalid field edge cases ───────────────────────────────────────

    [Fact]
    public void ValidateBalance_NullInputCommitments_ReturnsFalse()
    {
        var r = new byte[32]; r[31] = 5;
        var outputCommitment = PedersenCommitment.Commit(new UInt256(100), r);

        var transfer = new ConfidentialTransfer
        {
            InputCommitments = null!,
            OutputCommitments = new[] { outputCommitment },
            BalanceProofBlinding = new byte[32]
        };

        // Should not throw, should return false
        var result = false;
        try { result = TransferValidator.ValidateBalance(transfer); } catch { }
        result.Should().BeFalse();
    }

    [Fact]
    public void ValidateBalance_NullOutputCommitments_ReturnsFalse()
    {
        var r = new byte[32]; r[31] = 5;
        var inputCommitment = PedersenCommitment.Commit(new UInt256(100), r);

        var transfer = new ConfidentialTransfer
        {
            InputCommitments = new[] { inputCommitment },
            OutputCommitments = null!,
            BalanceProofBlinding = new byte[32]
        };

        var result = false;
        try { result = TransferValidator.ValidateBalance(transfer); } catch { }
        result.Should().BeFalse();
    }

    [Fact]
    public void ValidateBalance_WrongSizeBalanceBlinding_ReturnsFalse()
    {
        var r1 = new byte[32]; r1[31] = 5;
        var r2 = new byte[32]; r2[31] = 3;

        var inputCommitment = PedersenCommitment.Commit(new UInt256(100), r1);
        var outputCommitment = PedersenCommitment.Commit(new UInt256(100), r2);

        var transfer = new ConfidentialTransfer
        {
            InputCommitments = new[] { inputCommitment },
            OutputCommitments = new[] { outputCommitment },
            BalanceProofBlinding = new byte[31] // wrong size
        };

        TransferValidator.ValidateBalance(transfer).Should().BeFalse();
    }

    [Fact]
    public void ValidateBalance_WrongSizeInputCommitment_ReturnsFalse()
    {
        var r2 = new byte[32]; r2[31] = 3;
        var outputCommitment = PedersenCommitment.Commit(new UInt256(100), r2);

        var transfer = new ConfidentialTransfer
        {
            InputCommitments = new[] { new byte[47] }, // wrong size
            OutputCommitments = new[] { outputCommitment },
            BalanceProofBlinding = new byte[32]
        };

        TransferValidator.ValidateBalance(transfer).Should().BeFalse();
    }

    [Fact]
    public void ValidateBalance_WrongSizeOutputCommitment_ReturnsFalse()
    {
        var r1 = new byte[32]; r1[31] = 5;
        var inputCommitment = PedersenCommitment.Commit(new UInt256(100), r1);

        var transfer = new ConfidentialTransfer
        {
            InputCommitments = new[] { inputCommitment },
            OutputCommitments = new[] { new byte[47] }, // wrong size
            BalanceProofBlinding = new byte[32]
        };

        TransferValidator.ValidateBalance(transfer).Should().BeFalse();
    }

    [Fact]
    public void ValidateBalance_NullCommitmentInInputArray_ReturnsFalse()
    {
        var r2 = new byte[32]; r2[31] = 3;
        var outputCommitment = PedersenCommitment.Commit(new UInt256(100), r2);

        var transfer = new ConfidentialTransfer
        {
            InputCommitments = new byte[][] { null! },
            OutputCommitments = new[] { outputCommitment },
            BalanceProofBlinding = new byte[32]
        };

        TransferValidator.ValidateBalance(transfer).Should().BeFalse();
    }

    [Fact]
    public void ValidateBalance_NullCommitmentInOutputArray_ReturnsFalse()
    {
        var r1 = new byte[32]; r1[31] = 5;
        var inputCommitment = PedersenCommitment.Commit(new UInt256(100), r1);

        var transfer = new ConfidentialTransfer
        {
            InputCommitments = new[] { inputCommitment },
            OutputCommitments = new byte[][] { null! },
            BalanceProofBlinding = new byte[32]
        };

        TransferValidator.ValidateBalance(transfer).Should().BeFalse();
    }

    // ── Multi-input/output scenarios ────────────────────────────────────────

    [Fact]
    public void ValidateBalance_TwoInputsOneOutput_Balanced_ReturnsTrue()
    {
        // Input1: 30, r=10; Input2: 20, r=8; Output: 50, r=12
        // BalanceBlinding = (10+8) - 12 = 6
        var r1 = new byte[32]; r1[31] = 10;
        var r2 = new byte[32]; r2[31] = 8;
        var r3 = new byte[32]; r3[31] = 12;

        var in1 = PedersenCommitment.Commit(new UInt256(30), r1);
        var in2 = PedersenCommitment.Commit(new UInt256(20), r2);
        var out1 = PedersenCommitment.Commit(new UInt256(50), r3);

        var balanceBlinding = new byte[32]; balanceBlinding[31] = 6;

        var transfer = new ConfidentialTransfer
        {
            InputCommitments = new[] { in1, in2 },
            OutputCommitments = new[] { out1 },
            BalanceProofBlinding = balanceBlinding
        };

        TransferValidator.ValidateBalance(transfer).Should().BeTrue();
    }

    [Fact]
    public void ValidateBalance_OneInputTwoOutputs_Balanced_ReturnsTrue()
    {
        // Input: 50, r=20; Output1: 30, r=8; Output2: 20, r=5
        // BalanceBlinding = 20 - (8+5) = 7
        var r1 = new byte[32]; r1[31] = 20;
        var r2 = new byte[32]; r2[31] = 8;
        var r3 = new byte[32]; r3[31] = 5;

        var in1 = PedersenCommitment.Commit(new UInt256(50), r1);
        var out1 = PedersenCommitment.Commit(new UInt256(30), r2);
        var out2 = PedersenCommitment.Commit(new UInt256(20), r3);

        var balanceBlinding = new byte[32]; balanceBlinding[31] = 7;

        var transfer = new ConfidentialTransfer
        {
            InputCommitments = new[] { in1 },
            OutputCommitments = new[] { out1, out2 },
            BalanceProofBlinding = balanceBlinding
        };

        TransferValidator.ValidateBalance(transfer).Should().BeTrue();
    }

    [Fact]
    public void ValidateBalance_ThreeInputsThreeOutputs_Balanced_ReturnsTrue()
    {
        // Inputs: 10 (r=2), 20 (r=3), 30 (r=4) => total value 60, total r = 9
        // Outputs: 25 (r=1), 25 (r=5), 10 (r=1) => total value 60, total r = 7
        // BalanceBlinding = 9 - 7 = 2
        var ri1 = new byte[32]; ri1[31] = 2;
        var ri2 = new byte[32]; ri2[31] = 3;
        var ri3 = new byte[32]; ri3[31] = 4;
        var ro1 = new byte[32]; ro1[31] = 1;
        var ro2 = new byte[32]; ro2[31] = 5;
        var ro3 = new byte[32]; ro3[31] = 1;

        var in1 = PedersenCommitment.Commit(new UInt256(10), ri1);
        var in2 = PedersenCommitment.Commit(new UInt256(20), ri2);
        var in3 = PedersenCommitment.Commit(new UInt256(30), ri3);
        var out1 = PedersenCommitment.Commit(new UInt256(25), ro1);
        var out2 = PedersenCommitment.Commit(new UInt256(25), ro2);
        var out3 = PedersenCommitment.Commit(new UInt256(10), ro3);

        var balanceBlinding = new byte[32]; balanceBlinding[31] = 2;

        var transfer = new ConfidentialTransfer
        {
            InputCommitments = new[] { in1, in2, in3 },
            OutputCommitments = new[] { out1, out2, out3 },
            BalanceProofBlinding = balanceBlinding
        };

        TransferValidator.ValidateBalance(transfer).Should().BeTrue();
    }

    // ── ValidateTransfer integration ────────────────────────────────────────

    [Fact]
    public void ValidateTransfer_Unbalanced_ReturnsFalse()
    {
        var r1 = new byte[32]; r1[31] = 5;
        var r2 = new byte[32]; r2[31] = 3;

        var inputCommitment = PedersenCommitment.Commit(new UInt256(100), r1);
        var outputCommitment = PedersenCommitment.Commit(new UInt256(50), r2);

        var transfer = new ConfidentialTransfer
        {
            InputCommitments = new[] { inputCommitment },
            OutputCommitments = new[] { outputCommitment },
            BalanceProofBlinding = new byte[32]
        };

        TransferValidator.ValidateTransfer(transfer, null).Should().BeFalse();
    }

    [Fact]
    public void ValidateTransfer_NullTransfer_ReturnsFalse()
    {
        TransferValidator.ValidateTransfer(null!, null).Should().BeFalse();
    }

    // ── ValidateRangeProof ──────────────────────────────────────────────────

    [Fact]
    public void ValidateRangeProof_RangeProofPresent_NullVk_ReturnsFalse()
    {
        var transfer = CreateBalancedTransfer(amount: 10, inputBlindingValue: 5, outputBlindingValue: 2);

        // Attach a dummy range proof
        var g1 = PairingEngine.G1Generator;
        var g2 = PairingEngine.G2Generator;
        var rangeProofTransfer = new ConfidentialTransfer
        {
            InputCommitments = transfer.InputCommitments,
            OutputCommitments = transfer.OutputCommitments,
            BalanceProofBlinding = transfer.BalanceProofBlinding,
            RangeProof = new Groth16Proof { A = g1, B = g2, C = g1 }
        };

        // VK is null but range proof exists => should return false
        TransferValidator.ValidateRangeProof(rangeProofTransfer, null).Should().BeFalse();
    }

    [Fact]
    public void ValidateRangeProof_NullTransfer_ReturnsFalse()
    {
        // F-02: null?.RangeProof is null, so the method should return false
        // (range proofs are mandatory)
        TransferValidator.ValidateRangeProof(null!, null).Should().BeFalse();
    }

    [Fact]
    public void ValidateBalance_ZeroAmountTransfer_ReturnsTrue()
    {
        // Both input and output commit to 0
        var r1 = new byte[32]; r1[31] = 10;
        var r2 = new byte[32]; r2[31] = 3;

        var inputCommitment = PedersenCommitment.Commit(UInt256.Zero, r1);
        var outputCommitment = PedersenCommitment.Commit(UInt256.Zero, r2);

        var balanceBlinding = new byte[32]; balanceBlinding[31] = 7; // 10 - 3

        var transfer = new ConfidentialTransfer
        {
            InputCommitments = new[] { inputCommitment },
            OutputCommitments = new[] { outputCommitment },
            BalanceProofBlinding = balanceBlinding
        };

        TransferValidator.ValidateBalance(transfer).Should().BeTrue();
    }

    [Fact]
    public void ValidateBalance_EqualBlindingFactors_ZeroBalanceBlinding_ReturnsTrue()
    {
        // When input and output have the same blinding factor, BalanceBlinding = 0
        var r = new byte[32]; r[31] = 42;

        var inputCommitment = PedersenCommitment.Commit(new UInt256(100), r);
        var outputCommitment = PedersenCommitment.Commit(new UInt256(100), r);

        var transfer = new ConfidentialTransfer
        {
            InputCommitments = new[] { inputCommitment },
            OutputCommitments = new[] { outputCommitment },
            BalanceProofBlinding = new byte[32] // all zeros
        };

        TransferValidator.ValidateBalance(transfer).Should().BeTrue();
    }
}
