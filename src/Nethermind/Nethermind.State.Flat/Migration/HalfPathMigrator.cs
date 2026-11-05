// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nethermind.Config;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Timers;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.Migration;

/// <summary>Copies the HalfPath trie into flat in the background, advancing the <see cref="HalfPathMigrationCursor"/> chunk by chunk.</summary>
/// <remarks>
/// <para>
/// A chunk walks the trie of the persisted state in path order from the cursor, through a persistence reader that serves what is not
/// copied yet from the HalfPath DB, collecting about <see cref="IFlatDbConfig.MigrationChunkNodes"/> nodes outside any write batch. It writes them only if
/// the persisted state is still the one it walked, see <see cref="IPersistenceManager.TryWriteOnPersistedState"/>, so the copied values
/// are those readers of that state already see.
/// </para>
/// <para>
/// Chunks run on <see cref="IBackgroundTaskScheduler"/>, which cancels them when block processing starts, except while the node is
/// syncing, when they run alongside it. A cancelled chunk, a persist holding the lock or a persist since the walk drops the chunk,
/// which is walked again a little later.
/// </para>
/// </remarks>
public sealed class HalfPathMigrator(
    IPersistenceManager persistenceManager,
    HalfPathMigrationCursor cursor,
    IFlatDbConfig flatDbConfig,
    IBackgroundTaskScheduler scheduler,
    ITimerFactory timerFactory,
    IProcessExitSource processExitSource,
    ILogManager logManager) : IDisposable
{
    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(60);

    private readonly ILogger _logger = logManager.GetClassLogger<HalfPathMigrator>();
    private readonly Core.Timers.ITimer _retryTimer = timerFactory.CreateTimer(RetryDelay);
    private readonly Chunk _chunk = new();
    private long _nextProgressLog;

    private readonly record struct ChunkRequest : IBackgroundTaskRequest<ChunkRequest>;

    /// <summary>Starts copying from the cursor, if a migration is in progress.</summary>
    public void Start()
    {
        if (cursor.Current is null) return;
        if (_logger.IsInfo) _logger.Info("Copying the HalfPath trie state into the flat DB in the background.");
        _retryTimer.AutoReset = false;
        _retryTimer.Elapsed += (_, _) => Schedule();
        Schedule();
    }

    /// <inheritdoc/>
    public void Dispose() => _retryTimer.Dispose();

    private void Schedule()
    {
        if (processExitSource.Token.IsCancellationRequested) return;
        if (!scheduler.TryScheduleTask(default(ChunkRequest), RunChunk, ChunkTimeout)) ScheduleLater();
    }

    /// <summary>Schedules the next chunk after <see cref="RetryDelay"/>; chunks run one at a time, so one timer serves them all.</summary>
    private void ScheduleLater() => _retryTimer.Start();

    private Task RunChunk(ChunkRequest request, CancellationToken token)
    {
        if (cursor.Current is not { } from) return Task.CompletedTask;

        try
        {
            if (CopyChunk(from, token)) Schedule();
        }
        catch (Exception e)
        {
            // Resumes from the cursor on the next start.
            if (_logger.IsError) _logger.Error($"The HalfPath to flat migration stopped at {from}.", e);
        }

        return Task.CompletedTask;
    }

    /// <summary>Walks and writes one chunk from <paramref name="from"/>.</summary>
    /// <returns>Whether more is left to copy.</returns>
    private bool CopyChunk(in MigrationCursor from, CancellationToken token)
    {
        StateId state;
        using (IPersistence.IPersistenceReader reader = persistenceManager.LeaseReader(ReaderFlags.FullScan))
        {
            state = reader.CurrentState;
            _chunk.Walk(reader, state.StateRoot, from, flatDbConfig.MigrationChunkNodes, token);
        }

        MigrationCursor walkedFrom = from;
        bool written = false;
        if (token.IsCancellationRequested || !persistenceManager.TryWriteOnPersistedState(state, batch =>
            {
                // A wipe meanwhile abandons the migration.
                if (cursor.Current != walkedFrom) return;
                _chunk.Write(batch);
                written = true;
            }))
        {
            ScheduleLater();
            return false;
        }

        if (!written) return false;
        Metrics.HalfPathMigratedNodes += _chunk.Count;

        if (_chunk.IsComplete)
        {
            cursor.Complete();
            if (_logger.IsInfo) _logger.Info("The HalfPath to flat migration is complete.");
            return false;
        }

        cursor.Advance(_chunk.Next);
        LogProgress(_chunk.Next);
        return true;
    }

    private void LogProgress(in MigrationCursor copied)
    {
        long now = Environment.TickCount64;
        if (!_logger.IsInfo || now < _nextProgressLog || copied.Account is not { } account) return;
        _nextProgressLog = now + (long)ProgressInterval.TotalMilliseconds;
        double progress = BinaryPrimitives.ReadUInt64BigEndian(account.Bytes) / (double)ulong.MaxValue;
        _logger.Info($"HalfPath to flat migration {progress:P2} copied, at account {account}.");
    }

    /// <summary>Collects, in path order, the nodes after a cursor until a node limit is reached at a leaf, and writes them.</summary>
    /// <remarks>
    /// The walk decodes each node straight from its RLP, which it copies from the reader into a buffer that every chunk reuses, as it does
    /// the entry list, so a chunk allocates nothing once both have grown to a chunk's size. Chunks run one at a time.
    /// </remarks>
    private sealed class Chunk
    {
        private const int KeyNibbles = 64;
        private static readonly ReadOnlySpanAction<byte, List<byte>> Append = static (rlp, buffer) => buffer.AddRange(rlp);

        private readonly List<byte> _rlp = [];
        private readonly List<Entry> _entries = [];
        private IPersistence.IPersistenceReader? _reader;
        private MigrationCursor _from;
        private int _limit;
        private CancellationToken _token;
        private bool _full;

        /// <summary>The number of nodes collected.</summary>
        public int Count => _entries.Count;

        /// <summary>The cursor once the collected nodes are written.</summary>
        public MigrationCursor Next { get; private set; }

        /// <summary>Whether the walk reached the end of the trie, so nothing is left once the collected nodes are written.</summary>
        public bool IsComplete => !_full && !_token.IsCancellationRequested;

        private Span<byte> Rlp => CollectionsMarshal.AsSpan(_rlp);

        /// <summary>Collects the nodes of the trie at <paramref name="root"/> after <paramref name="from"/> until <paramref name="limit"/> is reached at a leaf.</summary>
        /// <exception cref="TrieException">A node on the way is in neither flat nor the HalfPath DB.</exception>
        public void Walk(IPersistence.IPersistenceReader reader, in ValueHash256 root, in MigrationCursor from, int limit, CancellationToken token)
        {
            _rlp.Clear();
            _entries.Clear();
            (_reader, _from, _limit, _token, _full, Next) = (reader, from, limit, token, false, from);
            try
            {
                if (root != ValueKeccak.EmptyTreeHash) Load(null, TreePath.Empty, root);
            }
            finally
            {
                _reader = null;
            }
        }

        /// <summary>Writes the collected nodes and, for a leaf, the account or slot it holds.</summary>
        public void Write(IPersistence.IWriteBatch batch)
        {
            ReadOnlySpan<byte> rlp = Rlp;
            Hash256? storageAddress = null;
            foreach (Entry entry in _entries)
            {
                // A storage trie's nodes are consecutive, so its address is built once.
                if (entry.Address is { } address && (storageAddress is null || storageAddress.ValueHash256 != address)) storageAddress = new Hash256(address);
                Importer.WriteNode(batch, entry.Address is null ? null : storageAddress, entry.Path, rlp[entry.Rlp], entry.LeafPath, rlp[entry.LeafValue]);
            }
        }

        /// <summary>Copies the node <paramref name="hash"/> names at <paramref name="path"/> into the buffer and walks it.</summary>
        /// <param name="address">The account path of the storage trie, or <c>null</c> for the state trie.</param>
        private void Load(in ValueHash256? address, in TreePath path, in ValueHash256 hash)
        {
            int start = _rlp.Count;
            // The walk follows key order, which the HalfPath and flat keys both mostly keep.
            bool loaded = address is { } storage
                ? _reader!.TryLoadStorageRlp(storage, path, hash, ReadFlags.HintReadAhead, Append, _rlp)
                : _reader!.TryLoadStateRlp(path, hash, ReadFlags.HintReadAhead, Append, _rlp);
            if (!loaded) throw new TrieException($"Missing trie node {hash} at {path} of {address?.ToString() ?? "the state trie"}.");
            Visit(address, path, start.._rlp.Count);
        }

        /// <summary>Collects the node whose RLP is at <paramref name="node"/> in the buffer, then walks what is below it in path order.</summary>
        private void Visit(in ValueHash256? address, in TreePath path, Range node)
        {
            int start = node.Start.Value;
            RlpReader reader = new(Rlp[node]);
            reader.ReadSequenceLength();
            if (reader.PeekNumberOfItemsRemaining(maxSearch: 3) > 2)
            {
                Collect(address, path, node, null, default);

                // Located before any is walked, as walking a child can move the buffer.
                Span<Range> children = stackalloc Range[TrieNode.BranchesCount];
                foreach (ref Range child in children)
                {
                    int childStart = start + reader.Position;
                    reader.SkipItem();
                    child = childStart..(start + reader.Position);
                }

                for (int i = 0; i < children.Length; i++) VisitChild(address, path.Append(i), children[i]);
                return;
            }

            ReadOnlySpan<byte> hexPrefix = reader.DecodeByteArraySpan();
            Span<byte> nibbles = stackalloc byte[KeyNibbles];
            NodeRlp.ReadHexPrefix(hexPrefix, 0, nibbles);
            TreePath childPath = path.Append(nibbles[..NodeRlp.HexPrefixNibbleCount(hexPrefix)]);
            if (!NodeRlp.IsLeafHexPrefix(hexPrefix))
            {
                Collect(address, path, node, null, default);
                int childStart = start + reader.Position;
                reader.SkipItem();
                VisitChild(address, childPath, childStart..(start + reader.Position));
                return;
            }

            ReadOnlySpan<byte> value = reader.DecodeByteArraySpan();
            int valueEnd = start + reader.Position;
            if (Collect(address, path, node, childPath.Path, (valueEnd - value.Length)..valueEnd))
            {
                Next = address is { } storage ? new MigrationCursor(storage, childPath.Path) : new MigrationCursor(childPath.Path, null);
                _full = _entries.Count >= _limit;
            }

            if (address is not null) return;
            if (!AccountDecoder.Instance.TryDecodeStruct(value, out AccountStruct account)) throw new InvalidDataException($"The state leaf at {childPath} is not an account.");
            if (account.HasStorage && ShouldVisit(null, childPath)) Load(childPath.Path, TreePath.Empty, account.StorageRoot);
        }

        /// <summary>Walks the child whose reference is at <paramref name="child"/> in the buffer: a hash, an embedded node or nothing.</summary>
        private void VisitChild(in ValueHash256? address, in TreePath path, Range child)
        {
            RlpReader reader = new(Rlp[child]);
            if (reader.IsSequenceNext())
            {
                if (ShouldVisit(address, path)) Visit(address, path, child);
                return;
            }

            ReadOnlySpan<byte> hash = reader.DecodeByteArraySpan();
            if (!hash.IsEmpty && ShouldVisit(address, path)) Load(address, path, new ValueHash256(hash));
        }

        /// <summary>Whether the walk goes on into the subtree at <paramref name="path"/>, which it does, until the chunk is full, once the subtree holds a key at or past the cursor.</summary>
        /// <remarks>A storage trie is asked for with its account leaf's state path, which then decides, so the storage of the cursor's account is entered.</remarks>
        private bool ShouldVisit(in ValueHash256? address, in TreePath path)
        {
            if (_full || _token.IsCancellationRequested) return false;
            if (_from.Account is not { } account) return true;

            ValueHash256 upperBound = path.ToUpperBoundPath();
            if (address is not { } storage) return upperBound.CompareTo(account) >= 0;

            int order = storage.CompareTo(account);
            return order > 0 || (order == 0 && (_from.Slot is not { } slot || upperBound.CompareTo(slot) > 0));
        }

        private bool Collect(in ValueHash256? address, in TreePath path, Range rlp, in ValueHash256? leafPath, Range leafValue)
        {
            if (_from.IsCopied(address, path)) return false;
            _entries.Add(new Entry(address, path, rlp, leafPath, leafValue));
            return true;
        }

        /// <summary>A collected node, with its RLP and, for a leaf, its value located in the buffer.</summary>
        /// <param name="Address">The account path of the storage trie, or <c>null</c> for the state trie.</param>
        /// <param name="LeafPath">The full path of a leaf's key; <c>null</c> for a branch or an extension.</param>
        private readonly record struct Entry(ValueHash256? Address, TreePath Path, Range Rlp, ValueHash256? LeafPath, Range LeafValue);
    }
}
