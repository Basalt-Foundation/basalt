using Basalt.Storage.RocksDb;
using FluentAssertions;
using Xunit;

namespace Basalt.Storage.Tests;

/// <summary>
/// Whether the write-ahead log stays bounded.
///
/// It did not. RocksDB cannot delete a WAL file while any column family still holds data written in it,
/// and metadata, block_index, state, staking and default each take only a few bytes per block. None of
/// them ever fills its write buffer, so none ever flushes, so every WAL file since the node started is
/// pinned. A soak on the testnet measured 713MB of WAL against 45MB of real data, growing steadily and
/// never falling, which reads as a huge chain when the state is tiny.
/// </summary>
public class RocksDbWalRetentionTests
{
    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "basalt-wal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static long WalBytes(string path)
        => new DirectoryInfo(path).GetFiles("*.log").Sum(f => f.Length);

    /// <summary>
    /// Writes far more than the WAL ceiling, with a trickle into a column family that would never flush
    /// on its own alongside the bulk. The trickle is the whole point: it is what used to pin every file.
    /// </summary>
    [RocksDbFact]
    public void The_write_ahead_log_does_not_grow_without_bound()
    {
        var path = TempDir();
        try
        {
            using var store = new RocksDbStore(path);

            var payload = new byte[16 * 1024];
            Random.Shared.NextBytes(payload);

            // 512MB of writes against a 128MB ceiling. Without a ceiling every byte of this stays on
            // disk as WAL forever.
            for (int i = 0; i < 32_768; i++)
            {
                var key = BitConverter.GetBytes((long)i);
                store.Put(RocksDbStore.CF.TrieNodes, key, payload);

                // The sleepy column family, written a few bytes at a time exactly as a node writes its
                // chain metadata once per block.
                if (i % 16 == 0)
                    store.Put(RocksDbStore.CF.Metadata, key, BitConverter.GetBytes(i));
            }

            long wal = WalBytes(path);

            // Twice the 128MB ceiling. Measured behaviour settles between 85MB and 145MB no matter how
            // much is written, so the headroom absorbs background flushing and the always-live current
            // file without being so loose that a partial regression slips through. Uncapped, the WAL
            // tracks bytes written one for one: 2GB written measured 2052MB of WAL across 66 files.
            wal.Should().BeLessThan(256L * 1024 * 1024,
                "the WAL is capped, so old files are dropped once their column families flush");
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
