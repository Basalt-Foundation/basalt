using Basalt.Codec;
using Basalt.Core;

namespace Basalt.Sdk.Contracts;

/// <summary>
/// What a cross-contract call returns when the runtime executed a real contract and got its encoded
/// return data back.
///
/// It is a wrapper rather than a bare <c>byte[]</c> on purpose. <see cref="Context.CallContract{T}"/>
/// first asks whether the handler's result is already a <c>T</c>, which is how the SDK test host works,
/// and a bare byte array would answer yes to <c>CallContract&lt;byte[]&gt;</c> and hand back the
/// length-prefixed encoding as though it were the value.
/// </summary>
public sealed class EncodedCallResult(byte[] data)
{
    /// <summary>The callee's return data, encoded the way its generated dispatcher wrote it.</summary>
    public byte[] Data { get; } = data;

    /// <summary>
    /// Decodes the return data as <typeparamref name="T"/>, matching the encoding the contract
    /// generator emits for return values. An empty payload is the callee returning void, which decodes
    /// to the default so a caller asking for a value from a void method gets zero rather than a crash.
    /// </summary>
    public T Decode<T>()
    {
        if (Data.Length == 0)
            return default!;

        var reader = new BasaltReader(Data);

        object decoded = typeof(T) switch
        {
            var t when t == typeof(bool) => reader.ReadBool(),
            var t when t == typeof(byte) => reader.ReadByte(),
            var t when t == typeof(ushort) => reader.ReadUInt16(),
            var t when t == typeof(uint) => reader.ReadUInt32(),
            var t when t == typeof(int) => reader.ReadInt32(),
            var t when t == typeof(ulong) => reader.ReadUInt64(),
            var t when t == typeof(long) => reader.ReadInt64(),
            var t when t == typeof(string) => reader.ReadString(),
            var t when t == typeof(byte[]) => reader.ReadBytes().ToArray(),
            var t when t == typeof(Address) => reader.ReadAddress(),
            var t when t == typeof(Hash256) => reader.ReadHash256(),
            var t when t == typeof(UInt256) => reader.ReadUInt256(),

            // Refused rather than guessed. Returning the default here would turn an unsupported return
            // type into a silent zero, and a contract reading a zero balance it never checked for is the
            // failure this whole path exists to avoid.
            _ => throw new ContractRevertException(
                $"Cross-contract call return type not supported: {typeof(T).Name}"),
        };

        return (T)decoded;
    }
}
