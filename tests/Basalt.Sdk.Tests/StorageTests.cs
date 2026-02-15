using Basalt.Sdk.Contracts;
using Basalt.Sdk.Testing;
using FluentAssertions;
using Xunit;

namespace Basalt.Sdk.Tests;

public class StorageTests : IDisposable
{
    private readonly BasaltTestHost _host = new();

    // --- StorageValue<T> Tests ---

    [Fact]
    public void StorageValue_SetGet_RoundTrip()
    {
        var value = new StorageValue<ulong>("test_value");

        value.Set(42UL);
        value.Get().Should().Be(42UL);
    }

    [Fact]
    public void StorageValue_DefaultsToZero()
    {
        var value = new StorageValue<ulong>("unset_value");
        value.Get().Should().Be(0UL);
    }

    [Fact]
    public void StorageValue_Overwrite_ReturnsLatest()
    {
        var value = new StorageValue<int>("overwrite_test");

        value.Set(10);
        value.Set(20);
        value.Get().Should().Be(20);
    }

    [Fact]
    public void StorageValue_DifferentKeys_Independent()
    {
        var a = new StorageValue<ulong>("key_a");
        var b = new StorageValue<ulong>("key_b");

        a.Set(100);
        b.Set(200);

        a.Get().Should().Be(100);
        b.Get().Should().Be(200);
    }

    // --- StorageMap<TKey, TValue> Tests ---

    [Fact]
    public void StorageMap_SetGet_RoundTrip()
    {
        var map = new StorageMap<string, ulong>("test_map");

        map.Set("alice", 500UL);
        map.Get("alice").Should().Be(500UL);
    }

    [Fact]
    public void StorageMap_ContainsKey_ReturnsTrueWhenSet()
    {
        var map = new StorageMap<string, string>("map_contains");

        map.ContainsKey("key1").Should().BeFalse();

        map.Set("key1", "value1");
        map.ContainsKey("key1").Should().BeTrue();
    }

    [Fact]
    public void StorageMap_ContainsKey_ReturnsFalseForUnsetKey()
    {
        var map = new StorageMap<string, string>("map_nokey");
        map.ContainsKey("nonexistent").Should().BeFalse();
    }

    [Fact]
    public void StorageMap_Delete_RemovesKey()
    {
        var map = new StorageMap<string, ulong>("map_delete");

        map.Set("to_delete", 999UL);
        map.ContainsKey("to_delete").Should().BeTrue();

        map.Delete("to_delete");
        map.ContainsKey("to_delete").Should().BeFalse();
        map.Get("to_delete").Should().Be(0UL); // Default for ulong
    }

    [Fact]
    public void StorageMap_MultipleKeys_Independent()
    {
        var map = new StorageMap<string, ulong>("map_multi");

        map.Set("alice", 100);
        map.Set("bob", 200);
        map.Set("carol", 300);

        map.Get("alice").Should().Be(100);
        map.Get("bob").Should().Be(200);
        map.Get("carol").Should().Be(300);
    }

    [Fact]
    public void StorageMap_DifferentPrefixes_Independent()
    {
        var balances = new StorageMap<string, ulong>("balances");
        var allowances = new StorageMap<string, ulong>("allowances");

        balances.Set("alice", 1000);
        allowances.Set("alice", 500);

        balances.Get("alice").Should().Be(1000);
        allowances.Get("alice").Should().Be(500);
    }

    [Fact]
    public void StorageMap_Overwrite_ReturnsLatest()
    {
        var map = new StorageMap<string, string>("map_overwrite");

        map.Set("key", "first");
        map.Set("key", "second");
        map.Get("key").Should().Be("second");
    }

    // --- StorageList<T> Tests ---

    [Fact]
    public void StorageList_AddGet_RoundTrip()
    {
        var list = new StorageList<int>("test_list");

        list.Add(10);
        list.Add(20);
        list.Add(30);

        list.Get(0).Should().Be(10);
        list.Get(1).Should().Be(20);
        list.Get(2).Should().Be(30);
    }

    [Fact]
    public void StorageList_Count_ReturnsCorrectCount()
    {
        var list = new StorageList<ulong>("count_list");

        list.Count.Should().Be(0);

        list.Add(1);
        list.Count.Should().Be(1);

        list.Add(2);
        list.Count.Should().Be(2);

        list.Add(3);
        list.Count.Should().Be(3);
    }

    [Fact]
    public void StorageList_Set_OverwritesAtIndex()
    {
        var list = new StorageList<int>("set_list");

        list.Add(100);
        list.Add(200);

        list.Set(0, 999);
        list.Get(0).Should().Be(999);
        list.Get(1).Should().Be(200);
        list.Count.Should().Be(2);
    }

    [Fact]
    public void StorageList_EmptyList_CountIsZero()
    {
        var list = new StorageList<int>("empty_list");
        list.Count.Should().Be(0);
    }

    // --- ContractStorage Snapshot/Restore Tests ---

    [Fact]
    public void ContractStorage_Snapshot_Restore()
    {
        var value = new StorageValue<ulong>("snap_val");
        var map = new StorageMap<string, ulong>("snap_map");

        value.Set(100);
        map.Set("key", 200);

        var snapshot = ContractStorage.Snapshot();

        // Modify after snapshot
        value.Set(999);
        map.Set("key", 888);
        map.Set("new_key", 777);

        value.Get().Should().Be(999);
        map.Get("key").Should().Be(888);

        // Restore snapshot
        ContractStorage.Restore(snapshot);

        value.Get().Should().Be(100);
        map.Get("key").Should().Be(200);
        map.ContainsKey("new_key").Should().BeFalse();
    }

    [Fact]
    public void ContractStorage_Clear_RemovesEverything()
    {
        var map = new StorageMap<string, ulong>("clear_map");
        map.Set("key1", 100);
        map.Set("key2", 200);

        ContractStorage.Clear();

        map.ContainsKey("key1").Should().BeFalse();
        map.ContainsKey("key2").Should().BeFalse();
        map.Get("key1").Should().Be(0UL);
    }

    [Fact]
    public void ContractStorage_Set_Get_Direct()
    {
        ContractStorage.Set("direct_key", "direct_value");
        ContractStorage.Get<string>("direct_key").Should().Be("direct_value");
    }

    [Fact]
    public void ContractStorage_ContainsKey_Direct()
    {
        ContractStorage.ContainsKey("nope").Should().BeFalse();
        ContractStorage.Set("yep", 42);
        ContractStorage.ContainsKey("yep").Should().BeTrue();
    }

    [Fact]
    public void ContractStorage_Delete_Direct()
    {
        ContractStorage.Set("del_key", 1);
        ContractStorage.ContainsKey("del_key").Should().BeTrue();

        ContractStorage.Delete("del_key");
        ContractStorage.ContainsKey("del_key").Should().BeFalse();
    }

    [Fact]
    public void ContractStorage_Snapshot_IsIndependentCopy()
    {
        ContractStorage.Set("snap_test", 10);
        var snap = ContractStorage.Snapshot();

        // Modify after snapshot - should NOT affect snapshot
        ContractStorage.Set("snap_test", 99);

        // Restore - should go back to 10
        ContractStorage.Restore(snap);
        ContractStorage.Get<int>("snap_test").Should().Be(10);
    }

    public void Dispose() => _host.Dispose();
}
