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
    public byte[] Encode()
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)NodeType);

        switch (NodeType)
        {
            case TrieNodeType.Empty:
                break;

            case TrieNodeType.Leaf:
            {
                var encodedPath = Path.ToCompactEncoding(isLeaf: true);
                WriteLength(ms, encodedPath.Length);
                ms.Write(encodedPath);
                WriteLength(ms, Value!.Length);
                ms.Write(Value);
                break;
            }

            case TrieNodeType.Extension:
            {
                var encodedPath = Path.ToCompactEncoding(isLeaf: false);
                WriteLength(ms, encodedPath.Length);
                ms.Write(encodedPath);
                Span<byte> hashBytes = stackalloc byte[Hash256.Size];
                ChildHash!.Value.WriteTo(hashBytes);
                ms.Write(hashBytes);
                break;
            }

            case TrieNodeType.Branch:
            {
                // Bitmap: which children are present (2 bytes for 16 bits)
                ushort bitmap = 0;
                for (int i = 0; i < 16; i++)
                {
                    if (Children[i].HasValue)
                        bitmap |= (ushort)(1 << i);
                }
                ms.WriteByte((byte)(bitmap >> 8));
                ms.WriteByte((byte)(bitmap & 0xFF));

                // Write present children
                Span<byte> hashBuf = stackalloc byte[Hash256.Size];
                for (int i = 0; i < 16; i++)
                {
                    if (Children[i].HasValue)
                    {
                        Children[i]!.Value.WriteTo(hashBuf);
                        ms.Write(hashBuf);
                    }
                }

                // Branch value
                if (BranchValue != null)
                {
                    ms.WriteByte(1);
                    WriteLength(ms, BranchValue.Length);
                    ms.Write(BranchValue);
                }
                else
                {
                    ms.WriteByte(0);
                }
                break;
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Decode a node from bytes.
    /// </summary>
    public static TrieNode Decode(byte[] data)
    {
        int pos = 0;
        var type = (TrieNodeType)data[pos++];

        switch (type)
        {
            case TrieNodeType.Empty:
                return CreateEmpty();

            case TrieNodeType.Leaf:
            {
                int pathLen = ReadLength(data, ref pos);
                var encodedPath = data.AsSpan(pos, pathLen).ToArray();
                pos += pathLen;
                var (path, _) = NibblePath.FromCompactEncoding(encodedPath);

                int valueLen = ReadLength(data, ref pos);
                var value = data.AsSpan(pos, valueLen).ToArray();
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
                var encodedPath = data.AsSpan(pos, pathLen).ToArray();
                pos += pathLen;
                var (path, _) = NibblePath.FromCompactEncoding(encodedPath);

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
                ushort bitmap = (ushort)((data[pos] << 8) | data[pos + 1]);
                pos += 2;

                var node = new TrieNode { NodeType = TrieNodeType.Branch };

                for (int i = 0; i < 16; i++)
                {
                    if ((bitmap & (1 << i)) != 0)
                    {
                        node.Children[i] = new Hash256(data.AsSpan(pos, Hash256.Size));
                        pos += Hash256.Size;
                    }
                }

                byte hasValue = data[pos++];
                if (hasValue == 1)
                {
                    int valueLen = ReadLength(data, ref pos);
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
        uint result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = data[pos++];
            result |= (uint)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return (int)result;
    }
}
