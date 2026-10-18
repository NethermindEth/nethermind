// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using NSubstitute;

/// <summary>
/// A flat-state RocksDB much larger than its block cache, read through the production
/// <see cref="FlatWorldStateScope"/>. Every fourth account has <see cref="SlotsPerAccount"/> slots; every
/// 32nd account is absent. Values are derived from the index, so every read is checked.
/// </summary>
sealed class ColdStateFixture : IDisposable
{
    public const int SlotsPerAccount = 16;
    private static readonly Hash256 StorageRoot = Keccak.Compute("async-prefetch-storage");
    private readonly string _directory;
    private readonly ResourcePool _resources = new(new FlatDbConfig());
    private readonly WarmReadPool _pool = new(Math.Min(4 * Environment.ProcessorCount, 64));
    private readonly bool _dropCaches;
    private ColumnsDb<FlatDbColumns> _db = null!;

    public ColdStateFixture(string directory, bool dropCaches)
    {
        _directory = directory;
        _dropCaches = dropCaches;
    }

    public int Accounts { get; private set; }

    public static Address AddressAt(int index)
    {
        byte[] bytes = new byte[20];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), index + 1);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(0), unchecked((int)0x9e3779b9 * (index + 1)));
        return new Address(bytes);
    }

    public static bool HasSlots(int index) => index % 4 == 0;
    public static UInt256 SlotKey(int slot) => (UInt256)((ulong)slot * 7919 + 1);

    public static Account? AccountAt(int index) => index % 32 == 31 ? null
        : new Account((ulong)index, new UInt256((ulong)index, 0xabcdef, 0x123456, 0xfedcba),
            HasSlots(index) ? StorageRoot : Keccak.EmptyTreeHash, Keccak.OfAnEmptyString);

    public static UInt256 SlotAt(int account, int slot) =>
        new((ulong)(account + 1), (ulong)(slot + 1), 0x123456789abcdef, 0xfedcba987654321);

    public void Build(int accounts)
    {
        Accounts = accounts;
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        Directory.CreateDirectory(_directory);
        Open();
        Stopwatch clock = Stopwatch.StartNew();
        IPersistence persistence = new RocksDbPersistence(_db, LimboLogs.Instance);
        const int chunk = 50_000;
        for (int start = 0; start < accounts; start += chunk)
        {
            using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis);
            int end = Math.Min(start + chunk, accounts);
            for (int i = start; i < end; i++)
            {
                Account? account = AccountAt(i);
                if (account is null) continue;
                Address address = AddressAt(i);
                batch.SetAccount(address, account);
                if (!HasSlots(i)) continue;
                for (int slot = 0; slot < SlotsPerAccount; slot++) batch.SetStorage(address, SlotKey(slot), SlotAt(i, slot));
            }
        }
        _db.Flush();
        _db.Compact();
        Console.WriteLine($"built {accounts:N0} accounts, {accounts / 4 * SlotsPerAccount:N0} slots in {clock.Elapsed.TotalSeconds:F0} s, " +
            $"{Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024 / 1024:N0} MB");
    }

    public void OpenExisting(int accounts)
    {
        Accounts = accounts;
        Open();
    }

    private void Open()
    {
        DbConfig config = new();
        RocksDbConfigFactory factory = new(config, new PruningConfig(), new HardwareInfo(), LimboLogs.Instance, validateConfig: false);
        _db = new(_directory, new DbSettings(DbNames.Flat, "flat"), config, factory, LimboLogs.Instance, Enum.GetValues<FlatDbColumns>());
    }

    /// <summary>Closes the database, empties the OS page cache and reopens with an empty block cache.</summary>
    public void ReopenCold()
    {
        _db.Dispose();
        if (_dropCaches)
        {
            ProcessStartInfo start = new("/usr/bin/sudo") { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string argument in new[] { "-n", "/bin/sh", "-c", "sync; echo 3 > /proc/sys/vm/drop_caches" }) start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("Cold-cache reset failed: " + process.StandardError.ReadToEnd());
        }
        Open();
    }

    public FlatWorldStateScope OpenScope(bool prefetch)
    {
        IPersistence persistence = new RocksDbPersistence(_db, LimboLogs.Instance);
        SnapshotPooledList snapshots = new(0);
        ReadOnlySnapshotBundle readOnly = new(snapshots, persistence.CreateReader(), false, PersistedSnapshotStack.Empty(false));
        SnapshotBundle bundle = new(readOnly, Substitute.For<ITrieNodeCache>(), _resources, ResourcePool.Usage.MainBlockProcessing);
        return new(StateId.PreGenesis, bundle, Substitute.For<IWorldStateScopeProvider.ICodeDb>(),
            Substitute.For<IFlatCommitTarget>(), new FlatDbConfig { AsyncReadPrefetch = prefetch }, new NoopTrieWarmer(), LimboLogs.Instance,
            new Lazy<WarmReadPool>(() => _pool));
    }

    public void Dispose()
    {
        _db.Dispose();
        _pool.Dispose();
    }

    private sealed class HardwareInfo : IHardwareInfo
    {
        public long AvailableMemoryBytes => 1L << 30;
        public int? MaxOpenFilesLimit => null;
    }
}

/// <summary>Checks every account and slot the access-list warmup delivers.</summary>
sealed class VerifyingSink(Dictionary<Address, int> indexes) : IWorldStateScopeProvider.IAsyncBalReaderSink
{
    public int AccountReads;
    public int SlotReads;
    public int Mismatches;

    public void OnAccountRead(Address address, Account? account)
    {
        if (account != ColdStateFixture.AccountAt(indexes[address])) Interlocked.Increment(ref Mismatches);
        Interlocked.Increment(ref AccountReads);
    }

    public void OnStorageRead(in StorageCell cell, in UInt256 value)
    {
        int account = indexes[cell.Address];
        int slot = (int)((cell.Index.u0 - 1) / 7919);
        if (value != ColdStateFixture.SlotAt(account, slot)) Interlocked.Increment(ref Mismatches);
        Interlocked.Increment(ref SlotReads);
    }

    public bool StillNeeded(Address address, out Account? account) { account = null; return true; }
    public bool StillNeeded(in StorageCell cell) => true;
}

/// <summary>One block's known read set: accounts with and without slots, as a block access list and as plain lists.</summary>
sealed class ReadSet
{
    public required int[] AccountIndexes { get; init; }
    public required int SlotsPerSlotAccount { get; init; }
    public required ReadOnlyBlockAccessList Bal { get; init; }
    public required Address[] Addresses { get; init; }
    public required StorageCell[] Slots { get; init; }
    public required Dictionary<Address, int> Indexes { get; init; }
    public int ExpectedSlotReads { get; init; }

    public static ReadSet Create(int totalAccounts, int slotAccounts, int plainAccounts, int slotsPerAccount, Random random)
    {
        HashSet<int> chosen = [];
        List<int> indexes = [];
        while (indexes.Count < slotAccounts)
        {
            int i = random.Next(totalAccounts / 4) * 4;
            if (chosen.Add(i)) indexes.Add(i);
        }
        while (indexes.Count < slotAccounts + plainAccounts)
        {
            int i = random.Next(totalAccounts);
            if (!ColdStateFixture.HasSlots(i) && chosen.Add(i)) indexes.Add(i);
        }

        ReadOnlyAccountChanges[] changes = new ReadOnlyAccountChanges[indexes.Count];
        Address[] addresses = new Address[indexes.Count];
        List<StorageCell> slots = [];
        Dictionary<Address, int> map = [];
        int expectedSlots = 0;
        for (int k = 0; k < indexes.Count; k++)
        {
            int i = indexes[k];
            Address address = addresses[k] = ColdStateFixture.AddressAt(i);
            map[address] = i;
            UInt256[] reads = ColdStateFixture.HasSlots(i) ? new UInt256[slotsPerAccount] : [];
            for (int s = 0; s < reads.Length; s++)
            {
                reads[s] = ColdStateFixture.SlotKey(s * (ColdStateFixture.SlotsPerAccount / slotsPerAccount));
                slots.Add(new StorageCell(address, reads[s]));
            }
            if (ColdStateFixture.AccountAt(i) is not null) expectedSlots += reads.Length;
            changes[k] = new(address, [], reads, [], [], []);
        }

        return new ReadSet
        {
            AccountIndexes = [.. indexes],
            SlotsPerSlotAccount = slotsPerAccount,
            Bal = new ReadOnlyBlockAccessList(changes, changes.Length),
            Addresses = addresses,
            Slots = [.. slots],
            Indexes = map,
            ExpectedSlotReads = expectedSlots,
        };
    }
}
