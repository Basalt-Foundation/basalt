using System.Buffers.Binary;
using System.Text;
using Basalt.Consensus.Staking;
using Basalt.Core;
using Basalt.Storage.RocksDb;

namespace Basalt.Node;

/// <summary>
/// B1: RocksDB-backed staking state persistence.
/// Key format:
///   Stakes:    0x01 + 20B address
///   Unbonding: 0x02 + 4B index (big-endian)
/// Value format (stakes):
///   SelfStake(32B) + DelegatedStake(32B) + TotalStake(32B) + IsActive(1B) +
///   RegisteredAtBlock(8B) + P2PEndpointLen(2B) + P2PEndpoint(UTF8) +
///   DelegatorCount(4B) + [DelegatorAddr(20B) + Amount(32B)]...
/// Value format (unbonding):
///   Validator(20B) + Amount(32B) + UnbondingCompleteBlock(8B)
/// </summary>
public sealed class RocksDbStakingPersistence : IStakingPersistence
{
    private readonly RocksDbStore _store;

    public RocksDbStakingPersistence(RocksDbStore store)
    {
        _store = store;
    }

    public void SaveStakes(IReadOnlyDictionary<Address, StakeInfo> stakes)
    {
        // Clear existing stakes first
        foreach (var (key, _) in _store.IteratePrefix(RocksDbStore.CF.Staking, [0x01]))
            _store.Delete(RocksDbStore.CF.Staking, key);

        foreach (var (addr, info) in stakes)
        {
            var key = new byte[1 + 20];
            key[0] = 0x01;
            addr.WriteTo(key.AsSpan(1, 20));

            var value = SerializeStakeInfo(info);
            _store.Put(RocksDbStore.CF.Staking, key, value);
        }
    }

    public Dictionary<Address, StakeInfo> LoadStakes()
    {
        var stakes = new Dictionary<Address, StakeInfo>();

        foreach (var (key, value) in _store.IteratePrefix(RocksDbStore.CF.Staking, [0x01]))
        {
            if (key.Length != 21) continue;
            var addr = new Address(key.AsSpan(1, 20));
            var info = DeserializeStakeInfo(addr, value);
            if (info != null)
                stakes[addr] = info;
        }

        return stakes;
    }

    public void SaveUnbondingQueue(IReadOnlyList<UnbondingEntry> queue)
    {
        // Clear existing unbonding entries first
        foreach (var (key, _) in _store.IteratePrefix(RocksDbStore.CF.Staking, [0x02]))
            _store.Delete(RocksDbStore.CF.Staking, key);

        for (int i = 0; i < queue.Count; i++)
        {
            var key = new byte[1 + 4];
            key[0] = 0x02;
            BinaryPrimitives.WriteInt32BigEndian(key.AsSpan(1, 4), i);

            var entry = queue[i];
            var value = new byte[20 + 32 + 8];
            entry.Validator.WriteTo(value.AsSpan(0, 20));
            entry.Amount.WriteTo(value.AsSpan(20, 32));
            BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(52, 8), entry.UnbondingCompleteBlock);

            _store.Put(RocksDbStore.CF.Staking, key, value);
        }
    }

    public List<UnbondingEntry> LoadUnbondingQueue()
    {
        var queue = new List<UnbondingEntry>();

        foreach (var (key, value) in _store.IteratePrefix(RocksDbStore.CF.Staking, [0x02]))
        {
            if (value.Length < 60) continue; // 20 + 32 + 8

            var validator = new Address(value.AsSpan(0, 20));
            var amount = new UInt256(value.AsSpan(20, 32));
            var completeBlock = BinaryPrimitives.ReadUInt64LittleEndian(value.AsSpan(52, 8));

            queue.Add(new UnbondingEntry
            {
                Validator = validator,
                Amount = amount,
                UnbondingCompleteBlock = completeBlock,
            });
        }

        return queue;
    }

    private static byte[] SerializeStakeInfo(StakeInfo info)
    {
        var endpointBytes = Encoding.UTF8.GetBytes(info.P2PEndpoint ?? "");
        var delegatorCount = info.Delegators.Count;
        var size = 32 + 32 + 32 + 1 + 8 + 2 + endpointBytes.Length + 4 + delegatorCount * (20 + 32);
        var buffer = new byte[size];
        var offset = 0;

        info.SelfStake.WriteTo(buffer.AsSpan(offset, 32)); offset += 32;
        info.DelegatedStake.WriteTo(buffer.AsSpan(offset, 32)); offset += 32;
        info.TotalStake.WriteTo(buffer.AsSpan(offset, 32)); offset += 32;
        buffer[offset++] = info.IsActive ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, 8), info.RegisteredAtBlock); offset += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset, 2), (ushort)endpointBytes.Length); offset += 2;
        endpointBytes.CopyTo(buffer.AsSpan(offset, endpointBytes.Length)); offset += endpointBytes.Length;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset, 4), delegatorCount); offset += 4;

        foreach (var (delegator, amount) in info.Delegators)
        {
            delegator.WriteTo(buffer.AsSpan(offset, 20)); offset += 20;
            amount.WriteTo(buffer.AsSpan(offset, 32)); offset += 32;
        }

        return buffer;
    }

    private static StakeInfo? DeserializeStakeInfo(Address addr, byte[] data)
    {
        if (data.Length < 32 + 32 + 32 + 1 + 8 + 2 + 4) return null; // minimum size

        var offset = 0;
        var selfStake = new UInt256(data.AsSpan(offset, 32)); offset += 32;
        var delegatedStake = new UInt256(data.AsSpan(offset, 32)); offset += 32;
        var totalStake = new UInt256(data.AsSpan(offset, 32)); offset += 32;
        var isActive = data[offset++] != 0;
        var registeredAtBlock = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8)); offset += 8;
        var endpointLen = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2)); offset += 2;

        if (offset + endpointLen + 4 > data.Length) return null;
        var endpoint = Encoding.UTF8.GetString(data.AsSpan(offset, endpointLen)); offset += endpointLen;
        var delegatorCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4)); offset += 4;

        var info = new StakeInfo
        {
            Address = addr,
            SelfStake = selfStake,
            DelegatedStake = delegatedStake,
            TotalStake = totalStake,
            IsActive = isActive,
            RegisteredAtBlock = registeredAtBlock,
            P2PEndpoint = endpoint,
        };

        for (int i = 0; i < delegatorCount && offset + 52 <= data.Length; i++)
        {
            var delegator = new Address(data.AsSpan(offset, 20)); offset += 20;
            var amount = new UInt256(data.AsSpan(offset, 32)); offset += 32;
            info.Delegators[delegator] = amount;
        }

        return info;
    }
}
