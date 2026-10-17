using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie.Pruning;

sealed class TrieCohortFixture : IDisposable
{
    private const int LeavesPerContract = 1024;
    private static readonly Action<FlatWorldStateScope> WaitForWarmups = typeof(FlatWorldStateScope)
        .GetMethod("WaitForOutstandingWarmups", BindingFlags.Instance | BindingFlags.NonPublic)!
        .CreateDelegate<Action<FlatWorldStateScope>>();
    private static readonly Func<FlatWorldStateScope, int> OutstandingWarmups = typeof(FlatWorldStateScope)
        .GetProperty("OutstandingWarmups", BindingFlags.Instance | BindingFlags.NonPublic)!.GetMethod!
        .CreateDelegate<Func<FlatWorldStateScope, int>>();
    private readonly string _directory;
    private readonly int _count;
    private readonly Hash256[] _roots;
    private readonly Hash256[] _expectedRoots;
    private readonly Address[] _addresses;
    private readonly UInt256[] _slots;
    private readonly UInt256[] _values;
    private readonly FlatDbConfig _config = new() { TrieWarmerWorkerCount = 2, ApplyStorageWritesOnIdleThread = false, DeferStorageTrieCommit = true };
    private readonly ResourcePool _resources;
    private readonly TrieWarmer _warmer;
    private ColumnsDb<FlatDbColumns> _db = null!;

    public TrieCohortFixture(int count, int contracts)
    {
        _count = count;
        _directory = Path.Combine(Environment.GetEnvironmentVariable("TRIE_DATA")!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _resources = new(_config);
        _warmer = new(LimboLogs.Instance, _config);
        _roots = new Hash256[contracts];
        _expectedRoots = new Hash256[contracts];
        _addresses = new Address[contracts];
        _slots = new UInt256[count];
        _values = new UInt256[contracts];
        for (int i = 0; i < count; i++) _slots[i] = (UInt256)(ulong)(((i / contracts) * 7919 + 73) & (LeavesPerContract - 1));
        OpenDatabase();
        IPersistence persistence = new RocksDbPersistence(_db, LimboLogs.Instance);
        using (IPersistence.IWriteBatch writer = persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis))
        {
            for (int contract = 0; contract < contracts; contract++)
            {
                byte[] address = new byte[20];
                BinaryPrimitives.WriteInt32BigEndian(address.AsSpan(16), contract + 1);
                _addresses[contract] = new(address);
                _values[contract] = new(Value(contract, changed: true), isBigEndian: true);
                Hash256 hash = _addresses[contract].ToAccountPath.ToHash256();
                StorageTree seed = new(new TriePrefetchFixture.SeedStore(writer, hash), LimboLogs.Instance);
                using MemDb expectedDb = new();
                StorageTree expected = new(new RawScopedTrieStore(expectedDb), LimboLogs.Instance);
                HashSet<UInt256> changed = [];
                for (int i = contract; i < count; i += contracts) changed.Add(_slots[i]);
                for (int i = 0; i < LeavesPerContract; i++)
                {
                    UInt256 slot = (UInt256)(ulong)i;
                    byte[] value = Value(contract, changed: false);
                    seed.Set(slot, value);
                    expected.Set(slot, Value(contract, changed.Contains(slot)));
                }
                seed.Commit();
                expected.UpdateRootHash();
                _roots[contract] = seed.RootHash;
                _expectedRoots[contract] = expected.RootHash;
            }
        }
        _db.Flush();
    }

    private static byte[] Value(int contract, bool changed) => [(byte)(contract + 1), changed ? (byte)0xbb : (byte)0xaa];

    private void OpenDatabase()
    {
        DbConfig config = new() { EnableDbStatistics = true };
        RocksDbConfigFactory factory = new(config, new PruningConfig(), new TriePrefetchFixture.HardwareInfo(), LimboLogs.Instance, validateConfig: false);
        _db = new(_directory, new DbSettings(DbNames.Flat, "flat"), config, factory, LimboLogs.Instance, Enum.GetValues<FlatDbColumns>());
    }

    public Iteration Prepare(string cache, bool probe = false)
    {
        if (cache == "cold-db")
        {
            _db.Dispose();
            ProcessStartInfo start = new("/usr/bin/sudo") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-n", "/bin/sh", "-c", "sync; echo 3 > /proc/sys/vm/drop_caches" }) start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException(process.StandardError.ReadToEnd());
            OpenDatabase();
        }
        TriePrefetchFixture.BatchProbe? counter = probe ? new() : null;
        IColumnsDb<FlatDbColumns> columns = counter is null ? _db : new TriePrefetchFixture.ProbeColumnsDb(_db, counter);
        IPersistence persistence = new RocksDbPersistence(columns, LimboLogs.Instance);
        bool detailed = Environment.GetEnvironmentVariable("DETAILED_METRICS") == "1";
        ReadOnlySnapshotBundle readOnly = new(new SnapshotPooledList(0), persistence.CreateReader(ReaderFlags.Sync), detailed, PersistedSnapshotStack.Empty(detailed));
        SnapshotBundle bundle = new(readOnly, new TriePrefetchFixture.EmptyTrieNodeCache(), _resources, ResourcePool.Usage.MainBlockProcessing);
        FlatWorldStateScope scope = new(new StateId(0, Keccak.EmptyTreeHash), bundle, null!, null!, _config, _warmer, LimboLogs.Instance);
        FlatStorageTree[] trees = new FlatStorageTree[_roots.Length];
        for (int i = 0; i < trees.Length; i++) trees[i] = new(scope, _warmer, bundle, _config, _roots[i], _addresses[i], LimboLogs.Instance);
        Iteration iteration = new(this, scope, trees, counter, cache);
        if (cache == "dedup-hints") iteration.Warm();
        counter?.Reset();
        return iteration;
    }

    public sealed class Iteration(TrieCohortFixture owner, FlatWorldStateScope scope, FlatStorageTree[] trees,
        TriePrefetchFixture.BatchProbe? counter, string cache) : IMeasuredIteration
    {
        public void Warm()
        {
            for (int i = 0; i < owner._count; i++) trees[i % trees.Length].HintSet(owner._slots[i]);
            WaitForWarmups(scope);
            if (OutstandingWarmups(scope) != 0) throw new InvalidOperationException("Cohort did not drain before the timed update");
        }

        public void Run()
        {
            Warm();
            for (int contract = 0; contract < trees.Length; contract++)
            {
                using IWorldStateScopeProvider.IStorageWriteBatch writer = trees[contract].CreateWriteBatch(owner._count / trees.Length, static (_, _) => { });
                UInt256 value = owner._values[contract];
                for (int i = contract; i < owner._count; i += trees.Length) writer.Set(owner._slots[i], value);
            }
        }

        public void Verify()
        {
            Run();
            for (int contract = 0; contract < trees.Length; contract++)
                if (trees[contract].RootHash != owner._expectedRoots[contract]) throw new InvalidOperationException($"Wrong storage root for contract {contract}");
            if (counter is null) throw new InvalidOperationException("Verification requires native probes");
            Console.WriteLine($"CohortNativeProbe Count={owner._count} Contracts={trees.Length} Cache={cache} Calls={counter.NativeCalls} ScalarCalls={counter.ScalarCalls} MaxKeys={counter.MaxKeys} MaxStoragePrefixes={counter.MaxStoragePrefixes}");
#if MIXED_TRIE
            if (owner._count == 512 && trees.Length == 64 && cache != "dedup-hints" && counter.MaxStoragePrefixes < 2)
                throw new InvalidOperationException("Candidate queue did not form a native MultiGet across contracts");
#endif
        }

        public void Dispose() => scope.Dispose();
    }

    public void Dispose()
    {
        _warmer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _db.Dispose();
        Directory.Delete(_directory, recursive: true);
    }
}
