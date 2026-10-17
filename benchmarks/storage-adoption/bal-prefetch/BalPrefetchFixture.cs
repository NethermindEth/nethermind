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
using Snapshot = Nethermind.State.Flat.Snapshot;

sealed class BalPrefetchFixture : IDisposable, IWorldStateScopeProvider.IAsyncBalReaderSink
{
    private static readonly Hash256 StorageRoot = Keccak.Compute("bal-prefetch-storage");
    private readonly string _directory;
    private readonly string _shape;
    private readonly ResourcePool _resources;
    private readonly WarmReadPool _pool = new(Math.Min(4 * Environment.ProcessorCount, 64));
    private readonly Address[] _addresses;
    private readonly Dictionary<Address, int> _indexes = [];
    private readonly ReadOnlyBlockAccessList _bal;
    private readonly int _slotsPerAccount;
    private ColumnsDb<FlatDbColumns> _db = null!;
    private CarryForwardCachingPersistence? _cache;
    private FlatWorldStateScope _scope = null!;
    private bool _verify;
    private int _accountReads;
    private int _slotReads;

    public BalPrefetchFixture(int count, string shape)
    {
        _shape = shape;
        _directory = Path.Combine(Environment.GetEnvironmentVariable("BAL_DATA")!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _resources = new ResourcePool(new FlatDbConfig());
        bool oneAccount = shape.StartsWith("one-account", StringComparison.Ordinal);
        int accounts = oneAccount ? 1 : count;
        _slotsPerAccount = shape == "accounts" ? 0 : oneAccount ? count : 8;
        _addresses = new Address[accounts];
        ReadOnlyAccountChanges[] changes = new ReadOnlyAccountChanges[accounts];
        for (int i = 0; i < accounts; i++)
        {
            byte[] bytes = new byte[20];
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), i + 1);
            Address address = _addresses[i] = new(bytes);
            _indexes.Add(address, i);
            UInt256[] slots = new UInt256[_slotsPerAccount];
            for (int j = 0; j < slots.Length; j++) slots[j] = (UInt256)(ulong)j;
            changes[i] = new(address, [], slots, [], [], []);
        }
        _bal = new(changes, changes.Length);
        OpenDatabase();
        IPersistence persistence = new RocksDbPersistence(_db, LimboLogs.Instance);
        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis))
        {
            for (int i = 0; i < accounts; i++)
            {
                batch.SetAccount(_addresses[i], AccountAt(i));
                if (AccountAt(i) is null) continue;
                for (int slot = 0; slot < _slotsPerAccount; slot++)
                    batch.SetStorage(_addresses[i], (UInt256)(ulong)slot, SlotAt(i, slot));
            }
        }
        _db.Flush();
        OpenScope();
    }

    private void OpenDatabase()
    {
        DbConfig config = new() { EnableDbStatistics = true };
        RocksDbConfigFactory factory = new(config, new PruningConfig(), new HardwareInfo(), LimboLogs.Instance, validateConfig: false);
        _db = new(_directory, new DbSettings(DbNames.Flat, "flat"), config, factory, LimboLogs.Instance, Enum.GetValues<FlatDbColumns>());
    }

    private void OpenScope()
    {
        IPersistence persistence = new RocksDbPersistence(_db, LimboLogs.Instance);
        if (_shape is "cache" or "one-account-cache") persistence = _cache = new CarryForwardCachingPersistence(persistence);
        SnapshotPooledList snapshots = new(1);
        if (_shape.StartsWith("overlay-", StringComparison.Ordinal))
        {
            SnapshotContent content = _resources.GetSnapshotContent(ResourcePool.Usage.MainBlockProcessing);
            for (int i = 0; i < _addresses.Length; i++)
            {
                if (i < int.Parse(_shape.AsSpan("overlay-".Length))) continue;
                content.Accounts[_addresses[i]] = AccountAt(i);
                for (int slot = 0; slot < _slotsPerAccount; slot++)
                    content.Storages[(_addresses[i], (UInt256)(ulong)slot)] = SlotAt(i, slot);
            }
            snapshots.Add(new Snapshot(StateId.PreGenesis, StateId.PreGenesis, content, _resources, ResourcePool.Usage.MainBlockProcessing));
        }
        bool detailed = Environment.GetEnvironmentVariable("DETAILED_METRICS") == "1";
        ReadOnlySnapshotBundle readOnly = new(snapshots, persistence.CreateReader(), detailed, PersistedSnapshotStack.Empty(detailed));
        SnapshotBundle bundle = new(readOnly, Substitute.For<ITrieNodeCache>(), _resources, ResourcePool.Usage.MainBlockProcessing);
        _scope = new(StateId.PreGenesis, bundle, Substitute.For<IWorldStateScopeProvider.ICodeDb>(),
            Substitute.For<IFlatCommitTarget>(), new FlatDbConfig(), new NoopTrieWarmer(), LimboLogs.Instance,
            new Lazy<WarmReadPool>(() => _pool));
    }

    public void Verify()
    {
        _verify = true;
        _accountReads = 0;
        _slotReads = 0;
        Read();
        int present = Enumerable.Range(0, _addresses.Length).Count(i => AccountAt(i) is not null);
        if (_accountReads != _addresses.Length || _slotReads != present * _slotsPerAccount)
            throw new InvalidOperationException($"Wrong sink counts: accounts={_accountReads}, slots={_slotReads}");
        _verify = false;
    }

    public void Read() => _scope.HintBal(_bal, this).GetAwaiter().GetResult();

    public void ReopenCold()
    {
        CloseScope();
        _db.Dispose();
        ProcessStartInfo start = new("/usr/bin/sudo") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in new[] { "-n", "/bin/sh", "-c", "sync; echo 3 > /proc/sys/vm/drop_caches" }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Cold-cache reset failed: " + process.StandardError.ReadToEnd());
        OpenDatabase();
        OpenScope();
    }

    public void OnAccountRead(Address address, Account? account)
    {
        if (_verify && account != AccountAt(_indexes[address])) throw new InvalidOperationException("Wrong account value: " + address);
        Interlocked.Increment(ref _accountReads);
    }

    public void OnStorageRead(in StorageCell cell, in UInt256 value)
    {
        if (_verify && value != SlotAt(_indexes[cell.Address], checked((int)cell.Index.u0)).GetValueOrDefault())
            throw new InvalidOperationException("Wrong slot value: " + cell);
        Interlocked.Increment(ref _slotReads);
    }

    public bool StillNeeded(Address address, out Account? account) { account = null; return true; }
    public bool StillNeeded(in StorageCell cell) => true;

    private static Account? AccountAt(int index) => index % 8 == 7 ? null
        : new Account((ulong)index, new UInt256((ulong)index, 0xabcdef, 0x123456, 0xfedcba), StorageRoot, Keccak.OfAnEmptyString);
    private static UInt256? SlotAt(int account, int slot) => slot % 5 == 4 ? null
        : new UInt256((ulong)(account + 1), (ulong)(slot + 1), 0x123456789abcdef, 0xfedcba987654321);

    private void CloseScope()
    {
        _scope.Dispose();
        _cache?.DisposeAsync().GetAwaiter().GetResult();
        _cache = null;
    }

    public void Dispose()
    {
        CloseScope();
        _pool.Dispose();
        _db.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class HardwareInfo : IHardwareInfo
    {
        public long AvailableMemoryBytes => 1L << 30;
        public int? MaxOpenFilesLimit => null;
    }
}
