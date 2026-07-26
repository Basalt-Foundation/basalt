using Basalt.Codec;
using Basalt.Core;
using Basalt.Sdk.Contracts;

namespace Basalt.Execution.VM;

/// <summary>
/// Turns the arguments of a <see cref="Context.CallContract{T}"/> into the call data the callee's
/// generated dispatcher expects.
///
/// The SDK hands these over as <c>object?[]</c>, because a contract writes
/// <c>CallContract(token, "Transfer", recipient, amount)</c> and the compiler boxes them. The dispatcher
/// on the other side reads a flat byte stream. This is the join, and it has to agree exactly with the
/// read expressions the contract generator emits: a mismatch is not a compile error anywhere, it is a
/// call that decodes into the wrong values.
/// </summary>
internal static class CrossContractArgumentEncoder
{
    /// <summary>Prefixes already-encoded arguments with the method's selector.</summary>
    public static byte[] WithSelector(string methodName, byte[] encodedArgs)
    {
        var callData = new byte[4 + encodedArgs.Length];
        SelectorHelper.ComputeSelectorBytes(methodName).CopyTo(callData, 0);
        encodedArgs.CopyTo(callData, 4);
        return callData;
    }

    /// <summary>Encodes selector plus arguments into dispatchable call data.</summary>
    public static byte[] Encode(string methodName, object?[] args)
    {
        var parts = new List<byte[]>(args.Length);
        var total = 4;

        foreach (object? arg in args)
        {
            byte[] encoded = EncodeOne(arg);
            parts.Add(encoded);
            total += encoded.Length;
        }

        var callData = new byte[total];
        SelectorHelper.ComputeSelectorBytes(methodName).CopyTo(callData, 0);

        var offset = 4;
        foreach (byte[] part in parts)
        {
            part.CopyTo(callData, offset);
            offset += part.Length;
        }

        return callData;
    }

    private static byte[] EncodeOne(object? arg)
    {
        var buffer = new byte[MaxArgumentSize(arg)];
        var writer = new BasaltWriter(buffer);

        switch (arg)
        {
            case bool v: writer.WriteBool(v); break;
            case byte v: writer.WriteByte(v); break;
            case ushort v: writer.WriteUInt16(v); break;
            case uint v: writer.WriteUInt32(v); break;
            case int v: writer.WriteInt32(v); break;
            case ulong v: writer.WriteUInt64(v); break;
            case long v: writer.WriteInt64(v); break;
            case string v: writer.WriteString(v); break;
            case byte[] v: writer.WriteBytes(v); break;
            case Address v: writer.WriteAddress(v); break;
            case Hash256 v: writer.WriteHash256(v); break;
            case UInt256 v: writer.WriteUInt256(v); break;

            // Refused rather than skipped. Dropping an argument would shift every argument after it and
            // still decode, which is the one failure mode worth going out of the way to make impossible.
            case null:
                throw new ContractRevertException("Cross-contract call argument is null");
            default:
                throw new ContractRevertException(
                    $"Cross-contract call argument type not supported: {arg.GetType().Name}");
        }

        return buffer[..writer.Position];
    }

    private static int MaxArgumentSize(object? arg) => arg switch
    {
        string v => 10 + System.Text.Encoding.UTF8.GetByteCount(v),
        byte[] v => 10 + v.Length,
        _ => 33,
    };
}
