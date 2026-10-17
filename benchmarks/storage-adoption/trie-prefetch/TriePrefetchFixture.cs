using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

sealed class TriePrefetchFixture : IDisposable
{
    private const int LeafCount = 4096;
    private readonly string _directory;
    private readonly Hash256? _address;
    private readonly ResourcePool _resources = new(new FlatDbConfig());
    private readonly byte[][] _keys;
    private readonly byte[][] _updates;
    private readonly Hash256 _root;
    private readonly Hash256 _expectedRoot;
    private ColumnsDb<FlatDbColumns> _db = null!;

    public TriePrefetchFixture(int count, string shape)
    {
        _directory = Path.Combine(Environment.GetEnvironmentVariable("TRIE_DATA")!, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _address = shape == "storage" ? Keccak.Compute("trie-bench-storage") : null;
        byte[][] allKeys = new byte[LeafCount][];
        for (int i = 0; i < allKeys.Length; i++)
        {
            byte[] raw = new byte[32];
            BinaryPrimitives.WriteInt32BigEndian(raw.AsSpan(28), i + 1);
            allKeys[i] = shape == "shared" ? raw : ValueKeccak.Compute(raw).Bytes.ToArray();
        }
        _keys = new byte[count][];
        _updates = new byte[count][];
        Dictionary<int, int> changed = [];
        for (int i = 0; i < count; i++)
        {
            int index = (i * 7919 + 73) & (LeafCount - 1);
            _keys[i] = allKeys[index];
            _updates[i] = LeafValue(index, changed: true);
            changed.Add(index, i);
        }
        OpenDatabase();
        IPersistence persistence = new RocksDbPersistence(_db, LimboLogs.Instance);
        using (IPersistence.IWriteBatch writer = persistence.CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis))
        {
            PatriciaTree seed = new(new SeedStore(writer, _address), LimboLogs.Instance);
            for (int i = 0; i < allKeys.Length; i++) seed.Set(allKeys[i], LeafValue(i, changed: false));
            seed.Commit();
            _root = seed.RootHash;
        }
        _db.Flush();
        using MemDb expectedDb = new();
        PatriciaTree expected = new(new RawScopedTrieStore(expectedDb), LimboLogs.Instance);
        for (int i = 0; i < allKeys.Length; i++) expected.Set(allKeys[i], LeafValue(i, changed.ContainsKey(i)));
        expected.UpdateRootHash();
        _expectedRoot = expected.RootHash;
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
        Action? verifyProbe = null;
        IColumnsDb<FlatDbColumns> columns = _db;
#if BATCH_TRIE
        if (probe)
        {
            BatchProbe counter = new();
            columns = new ProbeColumnsDb(_db, counter);
            verifyProbe = () =>
            {
                if (_keys.Length >= 64 && cache != "warm-trie" && counter.NativeCalls == 0)
                    throw new InvalidOperationException("Candidate preflight did not issue a native decoded MultiGet");
                if (cache == "warm-trie" && counter.NativeCalls != 0)
                    throw new InvalidOperationException("Warm-trie staging left native batch reads in the measured operation");
                Console.WriteLine($"NativeProbe Count={_keys.Length} Cache={cache} Calls={counter.NativeCalls} MaxKeys={counter.MaxKeys}");
            };
        }
#endif
        IPersistence persistence = new RocksDbPersistence(columns, LimboLogs.Instance);
        ReadOnlySnapshotBundle readOnly = new(new SnapshotPooledList(0), persistence.CreateReader(ReaderFlags.Sync), false, PersistedSnapshotStack.Empty());
        SnapshotBundle bundle = new(readOnly, new EmptyTrieNodeCache(), _resources, ResourcePool.Usage.MainBlockProcessing);
        Type type = typeof(FlatWorldStateScope).Assembly.GetType("Nethermind.State.Flat.ScopeProvider.StateTrieStoreWarmerAdapter", throwOnError: true)!;
        IScopedTrieStore store = (IScopedTrieStore)Activator.CreateInstance(type, [bundle])!;
        if (_address is not null) store = (IScopedTrieStore)store.GetStorageTrieNodeResolver(_address);
#if BATCH_TRIE
        if (store is not IBatchTrieNodeResolver) throw new InvalidOperationException("Candidate warmer lacks batch resolver capability");
#endif
        PatriciaTree tree = new(store, LimboLogs.Instance) { RootHash = _root };
        Iteration iteration = new(bundle, tree, _keys, _updates, _expectedRoot, verifyProbe);
        if (cache == "warm-trie") iteration.WarmScalar();
        return iteration;
    }

    private void OpenDatabase()
    {
        DbConfig config = new() { EnableDbStatistics = true };
        RocksDbConfigFactory factory = new(config, new PruningConfig(), new HardwareInfo(), LimboLogs.Instance, validateConfig: false);
        _db = new(_directory, new DbSettings(DbNames.Flat, "flat"), config, factory, LimboLogs.Instance, Enum.GetValues<FlatDbColumns>());
    }

    private static byte[] LeafValue(int index, bool changed)
    {
        byte[] value = new byte[64];
        BinaryPrimitives.WriteInt32BigEndian(value, index + 1);
        value[^1] = changed ? (byte)0xbb : (byte)0xaa;
        return value;
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    public sealed class Iteration : IDisposable
    {
        private readonly SnapshotBundle _bundle;
        private readonly PatriciaTree _tree;
        private readonly byte[][] _keys;
        private readonly byte[][] _updates;
        private readonly Hash256 _expected;
        private readonly Action? _verifyProbe;
#if BATCH_TRIE
        private readonly PatriciaTree.WarmUpEntry[] _entries;
#endif

        public Iteration(SnapshotBundle bundle, PatriciaTree tree, byte[][] keys, byte[][] updates, Hash256 expected, Action? verifyProbe)
        {
            _bundle = bundle;
            _tree = tree;
            _keys = keys;
            _updates = updates;
            _expected = expected;
            _verifyProbe = verifyProbe;
#if BATCH_TRIE
            _entries = new PatriciaTree.WarmUpEntry[keys.Length];
            for (int i = 0; i < keys.Length; i++) _entries[i] = new(new ValueHash256(keys[i]));
#endif
        }

        public void WarmScalar()
        {
            foreach (byte[] key in _keys) _tree.WarmUpPath(key);
        }

        public void Run()
        {
#if BATCH_TRIE
            if (_entries.Length >= 8) _tree.WarmUpPaths(_entries);
            else WarmScalar();
#else
            WarmScalar();
#endif
            for (int i = 0; i < _keys.Length; i++) _tree.Set(_keys[i], _updates[i]);
            _tree.UpdateRootHash();
        }

        public void Verify()
        {
            Run();
            if (_tree.RootHash != _expected) throw new InvalidOperationException($"Trie root mismatch: {_tree.RootHash} != {_expected}");
            for (int i = 0; i < _keys.Length; i++)
                if (!_tree.Get(_keys[i]).SequenceEqual(_updates[i])) throw new InvalidOperationException($"Wrong value at {i}");
            _verifyProbe?.Invoke();
        }

        public void Dispose() => _bundle.Dispose();
    }

    private sealed class SeedStore(IPersistence.IWriteBatch writer, Hash256? address) : AbstractMinimalTrieStore
    {
        public override TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => new(NodeType.Unknown, hash);
        public override byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => throw new InvalidOperationException("Seed must not read");
        public override ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => new SeedCommitter(writer, address);
    }

    private sealed class SeedCommitter(IPersistence.IWriteBatch writer, Hash256? address) : AbstractMinimalTrieStore.AbstractMinimalCommitter
    {
        protected override void WriteNode(in TreePath path, TrieNode node)
        {
            if (address is null) writer.SetStateTrieNode(path, node.FullRlp.AsSpan());
            else writer.SetStorageTrieNode(address, path, node.FullRlp.AsSpan());
        }

        protected override void PublishNodes(IEnumerable<List<(TreePath Path, TrieNode Node)>> buffers)
        {
            foreach (List<(TreePath Path, TrieNode Node)> buffer in buffers)
                foreach ((TreePath path, TrieNode node) in buffer) WriteNode(path, node);
        }
    }

    private sealed class HardwareInfo : IHardwareInfo
    {
        public long AvailableMemoryBytes => 1L << 30;
        public int? MaxOpenFilesLimit => null;
    }

    private sealed class EmptyTrieNodeCache : ITrieNodeCache
    {
        public bool TryGet(Hash256? address, in TreePath path, Hash256 hash, [NotNullWhen(true)] out TrieNode? node)
        {
            node = null;
            return false;
        }
        public void Add(TransientResource resource) { }
        public void Clear() { }
    }

#if BATCH_TRIE
    private sealed class BatchProbe
    {
        public int NativeCalls;
        public int MaxKeys;
    }

    private sealed class ProbeColumnsDb(IColumnsDb<FlatDbColumns> inner, BatchProbe counter) : IColumnsDb<FlatDbColumns>
    {
        public IDb GetColumnDb(FlatDbColumns key) => inner.GetColumnDb(key);
        public IEnumerable<FlatDbColumns> ColumnKeys => inner.ColumnKeys;
        public IColumnsWriteBatch<FlatDbColumns> StartWriteBatch() => inner.StartWriteBatch();
        public IColumnDbSnapshot<FlatDbColumns> CreateSnapshot() => new ProbeSnapshot(inner.CreateSnapshot(), counter);
        public IColumnDbSnapshot<FlatDbColumns> CreateSnapshot(bool sequentialReadAhead) => new ProbeSnapshot(inner.CreateSnapshot(sequentialReadAhead), counter);
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void Dispose() { }
    }

    private sealed class ProbeSnapshot(IColumnDbSnapshot<FlatDbColumns> inner, BatchProbe counter) : IColumnDbSnapshot<FlatDbColumns>
    {
        public IReadOnlyKeyValueStore GetColumn(FlatDbColumns key)
        {
            IReadOnlyKeyValueStore column = inner.GetColumn(key);
            return key is FlatDbColumns.StateTopNodes or FlatDbColumns.StateNodes or FlatDbColumns.StorageNodes or FlatDbColumns.FallbackNodes
                ? new ProbeStore(column, counter) : column;
        }
        public void Dispose() => inner.Dispose();
    }

    private sealed class ProbeStore(IReadOnlyKeyValueStore inner, BatchProbe counter) : IReadOnlyKeyValueStore, IBatchReadOnlyDb
    {
        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => inner.Get(key, flags);
        public Span<byte> GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => inner.GetSpan(key, flags);
        public void DangerousReleaseMemory(in ReadOnlySpan<byte> span) => inner.DangerousReleaseMemory(in span);
        public void GetMany<T, TDecoder>(ReadOnlySpan<byte> keys, int keyLength, Span<T> values, TDecoder decoder)
            where TDecoder : IValueDecoder<T>
        {
            if (inner is not IBatchReadOnlyDb native) throw new InvalidOperationException("Snapshot column lacks native MultiGet");
            counter.NativeCalls++;
            counter.MaxKeys = Math.Max(counter.MaxKeys, values.Length);
            native.GetMany(keys, keyLength, values, decoder);
        }
    }
#endif
}
