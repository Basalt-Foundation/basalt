using Basalt.Core;
using Basalt.Crypto;

namespace Basalt.Storage.Trie;

/// <summary>
/// Types of nodes in the Merkle Patricia Trie.
/// </summary>
public enum TrieNodeType : byte
{
    Empty = 0,
    Leaf = 1,
    Extension = 2,
    Branch = 3,
}

/// <summary>
/// A node in the Merkle Patricia Trie.
/// Modified Patricia Merkle Trie using BLAKE3 instead of Keccak.
/// </summary>
public sealed class TrieNode
{
    public TrieNodeType NodeType { get; private set; }

    // Branch node: 16 children + optional value
    public Hash256?[] Children { get; private set; } = new Hash256?[16];
    public byte[]? BranchValue { get; private set; }

    // Leaf/Extension node: path + value/child
    public NibblePath Path { get; private set; }
    public byte[]? Value { get; private set; }           // Leaf only
    public Hash256? ChildHash { get; private set; }      // Extension only

    private Hash256? _hash;
    public bool IsDirty { get; private set; } = true;

    private TrieNode() { }

    public static TrieNode CreateEmpty() => new() { NodeType = TrieNodeType.Empty };

    public static TrieNode CreateLeaf(NibblePath path, byte[] value)
    {
        return new TrieNode
        {
            NodeType = TrieNodeType.Leaf,
            Path = path,
            Value = value,
        };
    }

    public static TrieNode CreateExtension(NibblePath path, Hash256 childHash)
    {
        return new TrieNode
        {
            NodeType = TrieNodeType.Extension,
            Path = path,
            ChildHash = childHash,
        };
    }

    public static TrieNode CreateBranch()
    {
        return new TrieNode
        {
            NodeType = TrieNodeType.Branch,
        };
    }

    public void SetChild(int index, Hash256? hash)
    {
        Children[index] = hash;
        MarkDirty();
    }

    public void SetBranchValue(byte[]? value)
    {
        BranchValue = value;
        MarkDirty();
    }

    public void MarkDirty()
    {
        IsDirty = true;
        _hash = null;
    }

    /// <summary>
    /// Compute the BLAKE3 hash of this node.
    /// </summary>
    public Hash256 ComputeHash()
    {
        if (!IsDirty && _hash.HasValue)
            return _hash.Value;

        var encoded = Encode();
        _hash = Blake3Hasher.Hash(encoded);
        IsDirty = false;
        return _hash.Value;
    }

    /// <summary>
    /// Encode the node to bytes for hashing and storage.
    /// </summary>
    /// <remarks>
    /// L-02: Uses <see cref="MemoryStream"/> per call. For hot paths (block processing),
    /// a pre-computed size + direct <c>byte[]</c> write would reduce GC pressure.
    /// Branch node size is deterministic: 1 (type) + 2 (bitmap) + N*32 (children)
    /// + 1 (hasValue) + varint+value. Leaf/extension: 1 + varint+path + varint+value.
    /// </remarks>
    public byte[] Encode()
    {
        // Pre-compute exact size to avoid MemoryStream overhead
        int size = 1; // NodeType byte
        byte[]? encodedPath = null;
        ushort bitmap = 0;
        int childCount = 0;

        switch (NodeType)
        {
            case TrieNodeType.Empty:
                break;

            case TrieNodeType.Leaf:
                encodedPath = Path.ToCompactEncoding(isLeaf: true);
                size += VarIntSize(encodedPath.Length) + encodedPath.Length
                      + VarIntSize(Value!.Length) + Value.Length;
                break;

            case TrieNodeType.Extension:
                encodedPath = Path.ToCompactEncoding(isLeaf: false);
                size += VarIntSize(encodedPath.Length) + encodedPath.Length + Hash256.Size;
                break;

            case TrieNodeType.Branch:
                for (int i = 0; i < 16; i++)
                {
                    if (Children[i].HasValue)
                    {
                        bitmap |= (ushort)(1 << i);
                        childCount++;
                    }
                }
                size += 2 + childCount * Hash256.Size + 1; // bitmap + children + hasValue
                if (BranchValue != null)
                    size += VarIntSize(BranchValue.Length) + BranchValue.Length;
                break;
        }

        // Write directly to exact-size array
        var buffer = new byte[size];
        int pos = 0;
        buffer[pos++] = (byte)NodeType;

        switch (NodeType)
        {
            case TrieNodeType.Leaf:
                WriteVarInt(buffer, ref pos, encodedPath!.Length);
                encodedPath.CopyTo(buffer.AsSpan(pos));
                pos += encodedPath.Length;
                WriteVarInt(buffer, ref pos, Value!.Length);
                Value.CopyTo(buffer.AsSpan(pos));
                pos += Value.Length;
                break;

            case TrieNodeType.Extension:
                WriteVarInt(buffer, ref pos, encodedPath!.Length);
                encodedPath.CopyTo(buffer.AsSpan(pos));
                pos += encodedPath.Length;
                ChildHash!.Value.WriteTo(buffer.AsSpan(pos, Hash256.Size));
                pos += Hash256.Size;
                break;

            case TrieNodeType.Branch:
                buffer[pos++] = (byte)(bitmap >> 8);
                buffer[pos++] = (byte)(bitmap & 0xFF);
                for (int i = 0; i < 16; i++)
                {
                    if (Children[i].HasValue)
                    {
                        Children[i]!.Value.WriteTo(buffer.AsSpan(pos, Hash256.Size));
                        pos += Hash256.Size;
                    }
                }
                if (BranchValue != null)
                {
                    buffer[pos++] = 1;
                    WriteVarInt(buffer, ref pos, BranchValue.Length);
                    BranchValue.CopyTo(buffer.AsSpan(pos));
                }
                else
                {
                    buffer[pos++] = 0;
                }
                break;
        }

        return buffer;
    }

    private static int VarIntSize(int length)
    {
        var value = (uint)length;
        int size = 1;
        while (value >= 0x80) { size++; value >>= 7; }
        return size;
    }

    private static void WriteVarInt(byte[] buffer, ref int pos, int length)
    {
        var value = (uint)length;
        while (value >= 0x80)
        {
            buffer[pos++] = (byte)(value | 0x80);
            value >>= 7;
        }
        buffer[pos++] = (byte)value;
    }

    /// <summary>
    /// Decode a node from bytes.
    /// </summary>
    public static TrieNode Decode(byte[] data)
    {
        if (data.Length == 0)
            throw new InvalidDataException("Cannot decode trie node from empty data.");

        int pos = 0;
        var type = (TrieNodeType)data[pos++];

        switch (type)
        {
            case TrieNodeType.Empty:
                return CreateEmpty();

            case TrieNodeType.Leaf:
            {
                int pathLen = ReadLength(data, ref pos);
                EnsureBounds(data, pos, pathLen, "leaf path");
                var (path, _) = NibblePath.FromCompactEncoding(data.AsSpan(pos, pathLen));
                pos += pathLen;

                int valueLen = ReadLength(data, ref pos);
                EnsureBounds(data, pos, valueLen, "leaf value");
                var value = data.AsSpan(pos, valueLen).ToArray(); // value must be owned
                pos += valueLen;

                return new TrieNode
                {
                    NodeType = TrieNodeType.Leaf,
                    Path = path,
                    Value = value,
                    IsDirty = false,
                };
            }

            case TrieNodeType.Extension:
            {
                int pathLen = ReadLength(data, ref pos);
                EnsureBounds(data, pos, pathLen, "extension path");
                var (path, _) = NibblePath.FromCompactEncoding(data.AsSpan(pos, pathLen));
                pos += pathLen;

                EnsureBounds(data, pos, Hash256.Size, "extension child hash");
                var hash = new Hash256(data.AsSpan(pos, Hash256.Size));
                pos += Hash256.Size;

                return new TrieNode
                {
                    NodeType = TrieNodeType.Extension,
                    Path = path,
                    ChildHash = hash,
                    IsDirty = false,
                };
            }

            case TrieNodeType.Branch:
            {
                EnsureBounds(data, pos, 2, "branch bitmap");
                ushort bitmap = (ushort)((data[pos] << 8) | data[pos + 1]);
                pos += 2;

                var node = new TrieNode { NodeType = TrieNodeType.Branch };

                for (int i = 0; i < 16; i++)
                {
                    if ((bitmap & (1 << i)) != 0)
                    {
                        EnsureBounds(data, pos, Hash256.Size, $"branch child[{i}] hash");
                        node.Children[i] = new Hash256(data.AsSpan(pos, Hash256.Size));
                        pos += Hash256.Size;
                    }
                }

                EnsureBounds(data, pos, 1, "branch hasValue flag");
                byte hasValue = data[pos++];
                if (hasValue == 1)
                {
                    int valueLen = ReadLength(data, ref pos);
                    EnsureBounds(data, pos, valueLen, "branch value");
                    node.BranchValue = data.AsSpan(pos, valueLen).ToArray();
                    pos += valueLen;
                }

                node.IsDirty = false;
                return node;
            }

            default:
                throw new InvalidDataException($"Unknown trie node type: {type}");
        }
    }

    /// <summary>
    /// Validate that <paramref name="count"/> bytes are available at <paramref name="pos"/>.
    /// </summary>
    private static void EnsureBounds(byte[] data, int pos, int count, string fieldName)
    {
        if (pos < 0 || count < 0 || pos > data.Length || count > data.Length - pos)
            throw new InvalidDataException(
                $"Trie node decode: not enough data for {fieldName} " +
                $"(need {count} bytes at offset {pos}, but data length is {data.Length}).");
    }

    private static void WriteLength(MemoryStream ms, int length)
    {
        // Simple varint: 1-4 bytes
        var value = (uint)length;
        while (value >= 0x80)
        {
            ms.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        ms.WriteByte((byte)value);
    }

    private static int ReadLength(byte[] data, ref int pos)
    {
        // Varint decoding with bounds checks:
        // - Maximum 5 bytes for a 32-bit value (4 continuation + 1 terminal)
        // - At shift=28 (5th byte), only the low 4 bits are valid
        // - pos must be within data.Length before each read
        const int MaxVarintBytes = 5;

        uint result = 0;
        int shift = 0;

        for (int i = 0; i < MaxVarintBytes; i++)
        {
            if (pos >= data.Length)
                throw new InvalidDataException("Unexpected end of data while reading varint length.");

            byte b = data[pos++];

            if (shift == 28)
            {
                // 5th byte: only low 4 bits are valid for a 32-bit value,
                // and continuation bit must not be set.
                if ((b & 0xF0) != 0)
                    throw new InvalidDataException("Varint length overflow: value exceeds 32 bits.");

                result |= (uint)b << 28;
                if (result > int.MaxValue)
                    throw new InvalidDataException("Varint length overflow: value exceeds int.MaxValue.");
                return (int)result;
            }

            result |= (uint)(b & 0x7F) << shift;
            shift += 7;

            if ((b & 0x80) == 0)
                return (int)result;
        }

        throw new InvalidDataException("Varint length exceeds maximum of 5 bytes.");
    }
}
