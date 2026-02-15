namespace Basalt.Storage.Trie;

/// <summary>
/// Represents a path through the trie as a sequence of nibbles (4-bit values).
/// Each byte in the key produces two nibbles.
/// </summary>
public readonly struct NibblePath
{
    private readonly byte[] _data;
    private readonly int _offset;
    private readonly int _length;

    public NibblePath(byte[] data) : this(data, 0, data.Length * 2) { }

    public NibblePath(byte[] data, int offset, int length)
    {
        _data = data;
        _offset = offset;
        _length = length;
    }

    public int Length => _length;
    public bool IsEmpty => _length == 0;

    /// <summary>
    /// Get the nibble at the specified index.
    /// </summary>
    public byte this[int index]
    {
        get
        {
            int absoluteIndex = _offset + index;
            int byteIndex = absoluteIndex / 2;
            return (absoluteIndex % 2 == 0)
                ? (byte)(_data[byteIndex] >> 4)
                : (byte)(_data[byteIndex] & 0x0F);
        }
    }

    /// <summary>
    /// Create a sub-path starting from the given offset.
    /// </summary>
    public NibblePath Slice(int start)
    {
        return new NibblePath(_data, _offset + start, _length - start);
    }

    /// <summary>
    /// Create a sub-path with specified offset and length.
    /// </summary>
    public NibblePath Slice(int start, int length)
    {
        return new NibblePath(_data, _offset + start, length);
    }

    /// <summary>
    /// Compute the length of the common prefix between this path and another.
    /// </summary>
    public int CommonPrefixLength(NibblePath other)
    {
        int maxLen = Math.Min(_length, other._length);
        for (int i = 0; i < maxLen; i++)
        {
            if (this[i] != other[i])
                return i;
        }
        return maxLen;
    }

    /// <summary>
    /// Create a NibblePath from a byte array key.
    /// </summary>
    public static NibblePath FromKey(byte[] key) => new(key);

    /// <summary>
    /// Create a NibblePath from a span, making a copy.
    /// </summary>
    public static NibblePath FromSpan(ReadOnlySpan<byte> key) => new(key.ToArray());

    /// <summary>
    /// Encode the path with a prefix nibble for compact (hex-prefix) encoding.
    /// Used for serializing extension and leaf nodes.
    /// </summary>
    public byte[] ToCompactEncoding(bool isLeaf)
    {
        // Hex-prefix encoding:
        // - If even length: prefix with 0x00 (extension) or 0x20 (leaf)
        // - If odd length:  prefix with 0x1X (extension) or 0x3X (leaf), where X is first nibble
        bool isOdd = _length % 2 != 0;
        int resultLength = (_length + 2) / 2;
        var result = new byte[resultLength];

        int flags = isLeaf ? 2 : 0;
        if (isOdd)
        {
            flags |= 1;
            result[0] = (byte)((flags << 4) | this[0]);
            for (int i = 1; i < _length; i++)
            {
                int resultIndex = (i + 1) / 2;
                if (i % 2 != 0)
                    result[resultIndex] = (byte)(this[i] << 4);
                else
                    result[resultIndex] |= this[i];
            }
        }
        else
        {
            result[0] = (byte)(flags << 4);
            for (int i = 0; i < _length; i++)
            {
                int resultIndex = (i + 2) / 2;
                if (i % 2 == 0)
                    result[resultIndex] = (byte)(this[i] << 4);
                else
                    result[resultIndex] |= this[i];
            }
        }

        return result;
    }

    /// <summary>
    /// Decode a compact (hex-prefix) encoded path.
    /// </summary>
    public static (NibblePath Path, bool IsLeaf) FromCompactEncoding(byte[] encoded)
    {
        if (encoded.Length == 0)
            return (new NibblePath([], 0, 0), false);

        int firstNibble = encoded[0] >> 4;
        bool isLeaf = (firstNibble & 2) != 0;
        bool isOdd = (firstNibble & 1) != 0;

        // Compute nibble count
        int nibbleCount = (encoded.Length * 2) - (isOdd ? 1 : 2);

        var nibbles = new byte[(nibbleCount + 1) / 2];

        if (isOdd)
        {
            // First nibble is in the low part of encoded[0]
            int srcNibbleOffset = 1; // skip flags nibble
            for (int i = 0; i < nibbleCount; i++)
            {
                int srcAbsolute = srcNibbleOffset + i;
                int srcByte = srcAbsolute / 2;
                byte nibble = (srcAbsolute % 2 == 0)
                    ? (byte)(encoded[srcByte] >> 4)
                    : (byte)(encoded[srcByte] & 0x0F);

                int dstByte = i / 2;
                if (i % 2 == 0)
                    nibbles[dstByte] = (byte)(nibble << 4);
                else
                    nibbles[dstByte] |= nibble;
            }
        }
        else
        {
            // Skip first byte entirely (two flag nibbles)
            int srcNibbleOffset = 2;
            for (int i = 0; i < nibbleCount; i++)
            {
                int srcAbsolute = srcNibbleOffset + i;
                int srcByte = srcAbsolute / 2;
                byte nibble = (srcAbsolute % 2 == 0)
                    ? (byte)(encoded[srcByte] >> 4)
                    : (byte)(encoded[srcByte] & 0x0F);

                int dstByte = i / 2;
                if (i % 2 == 0)
                    nibbles[dstByte] = (byte)(nibble << 4);
                else
                    nibbles[dstByte] |= nibble;
            }
        }

        return (new NibblePath(nibbles, 0, nibbleCount), isLeaf);
    }

    public override string ToString()
    {
        var chars = new char[_length];
        for (int i = 0; i < _length; i++)
            chars[i] = "0123456789abcdef"[this[i]];
        return new string(chars);
    }

    public bool Equals(NibblePath other)
    {
        if (_length != other._length) return false;
        for (int i = 0; i < _length; i++)
        {
            if (this[i] != other[i]) return false;
        }
        return true;
    }
}
