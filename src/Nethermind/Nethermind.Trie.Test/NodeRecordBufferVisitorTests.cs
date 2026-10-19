// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.NodeBuffer;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[Parallelizable(ParallelScope.All)]
public class NodeRecordBufferVisitorTests
{
    [Test]
    public void Callbacks_match_existing_walker([Values(1, 4)] int parallelism, [Values] bool persistenceOnly, [Values] bool fullScan)
    {
        using NodeRecordBuffer sourceBuffer = new(0, 64 * 1024);
        TrieRoot root = Build(sourceBuffer, null, false);
        RecordingResolver persistence = new(sourceBuffer, root);
        RecordingVisitor expected = new() { IsFullDbScan = fullScan };
        PatriciaTree tree = new(persistence, LimboLogs.Instance);
        VisitingStats expectedStats = new();
        tree.Accept(expected, root.ToHash256(), diagnostics: expectedStats);
        persistence.Loads.Clear();
        persistence.RejectFind = true;
        using NodeRecordBuffer emptyBuffer = new(0, 64 * 1024);
        RecordingVisitor actual = new() { IsFullDbScan = fullScan };
        VisitingStats stats = new();
        new NodeRecordBufferVisitor<Context>(persistenceOnly ? emptyBuffer : sourceBuffer, actual,
            new VisitingOptions { MaxDegreeOfParallelism = parallelism }, stats).Accept(root.ToHash256(), persistence);

        using (Assert.EnterMultipleScope())
        {
            if (parallelism == 1) Assert.That(actual.Events, Is.EqualTo(expected.Events));
            else Assert.That(actual.Events, Is.EquivalentTo(expected.Events));
            Assert.That(persistence.Loads.Count, persistenceOnly ? Is.GreaterThan(0) : Is.Zero);
            Assert.That(stats.NodeLookups, Is.EqualTo(expectedStats.NodeLookups));
            Assert.That(stats.MaxDepth, Is.EqualTo(expectedStats.MaxDepth));
            Assert.That(stats.CacheMisses, Is.EqualTo(persistence.Loads.Count));
            Assert.That(stats.MaxDepth, Is.GreaterThan(0));
            Assert.That(actual.Events.Any(e => e.EndsWith(":inline")), Is.True);
        }
    }

    [Test]
    public void Unlinked_buffer_children_fall_back_to_persistence([Values] bool prune)
    {
        using NodeRecordBuffer sourceBuffer = new(0, 64 * 1024);
        TrieRoot root = Build(sourceBuffer, null, false);
        RecordingResolver persistence = new(sourceBuffer, root) { RejectFind = true };
        using NodeRecordBuffer buffer = new(0, 64 * 1024);
        Assert.That(sourceBuffer.TryLocate(root.Pointer, out NodeRecord rootRecord), Is.True);
        ulong pointer = buffer.AppendRecordFromRlp(null, TreePath.Empty, root.Hash, rootRecord.Rlp);
        buffer.SetRoot(null, root.Hash, pointer);
        RecordingVisitor visitor = new() { Prune = prune };
        VisitingStats stats = new();
        new NodeRecordBufferVisitor<Context>(buffer, visitor, diagnostics: stats).Accept(root.ToHash256(), persistence);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistence.Loads.Count, prune ? Is.Zero : Is.GreaterThan(0));
            Assert.That(stats.NodeLookups, Is.GreaterThan(1), "hashed references are counted even when pruned");
            Assert.That(stats.CacheMisses, Is.EqualTo(persistence.Loads.Count));
            Assert.That(visitor.Events.Any(e => e.StartsWith("missing")), Is.False);
        }
    }

    [Test]
    public void Missing_descendants_are_reported_at_their_context()
    {
        using NodeRecordBuffer sourceBuffer = new(0, 64 * 1024);
        TrieRoot root = Build(sourceBuffer, null, true);
        Assert.That(sourceBuffer.TryLocate(root.Pointer, out NodeRecord record), Is.True);
        using NodeRecordBuffer buffer = new(0, 64 * 1024);
        ulong pointer = buffer.AppendRecordFromRlp(null, TreePath.Empty, root.Hash, record.Rlp);
        buffer.SetRoot(null, root.Hash, pointer);
        RecordingVisitor visitor = new();
        new NodeRecordBufferVisitor<Context>(buffer, visitor).Accept(root.ToHash256(), new RecordingResolver { RejectFind = true });
        Assert.That(visitor.Events.Where(e => e.StartsWith("missing")), Is.EqualTo(new[] { "missing:0", "missing:1" }));
    }

    [Test]
    public void Visitor_exceptions_are_not_missing_nodes([Values] bool cancellation)
    {
        using NodeRecordBuffer buffer = new(0, 64 * 1024);
        TrieRoot root = Build(buffer, null, true);
        Exception failure = cancellation ? new OperationCanceledException() : new TrieException("visitor failure");
        RecordingVisitor visitor = new() { OnLeaf = _ => throw failure };
        Assert.That(() => new NodeRecordBufferVisitor<Context>(buffer, visitor).Accept(root.ToHash256(), new RecordingResolver { RejectFind = true }),
            Throws.Exception.SameAs(failure));
        Assert.That(visitor.Events.Any(e => e.StartsWith("missing")), Is.False);
    }

    [Test]
    public void Missing_and_empty_root_order_matches_existing_walker([Values] bool fullScan, [Values] bool empty)
    {
        using NodeRecordBuffer buffer = new(0, 64 * 1024);
        RecordingResolver persistence = new() { RejectFind = true };
        RecordingVisitor visitor = new() { IsFullDbScan = fullScan };
        Hash256 root = empty ? Keccak.EmptyTreeHash : Keccak.Zero;
        new NodeRecordBufferVisitor<Context>(buffer, visitor).Accept(root, persistence);
        string[] expected = empty ? ["tree"] : fullScan ? ["missing:"] : ["tree", "missing:"];
        Assert.That(visitor.Events, Is.EqualTo(expected));
    }

    [Test]
    public void Read_flags_reach_persistence([Values] bool fullScan)
    {
        using NodeRecordBuffer buffer = new(0, 64 * 1024);
        RecordingResolver persistence = new() { RejectFind = true };
        RecordingVisitor visitor = new() { IsFullDbScan = fullScan, ExtraReadFlag = ReadFlags.HintCacheMiss };
        new NodeRecordBufferVisitor<Context>(buffer, visitor).Accept(Keccak.Zero, persistence);
        Assert.That(persistence.Loads.Single(), Is.EqualTo(ReadFlags.HintCacheMiss | (fullScan ? ReadFlags.HintReadAhead : ReadFlags.None)));
    }

    [Test]
    public void Persisted_account_resolves_storage_root_from_buffer([Values] bool expectAccounts)
    {
        using NodeRecordBuffer buffer = new(0, 64 * 1024);
        Hash256 address = Key(0);
        TrieRoot storage = Build(buffer, address, false);
        RecordingResolver persistence = new();
        byte[] accountRlp = Rlp.Encode(new Account(1, 2, storage.ToHash256(), Keccak.OfAnEmptySequenceRlp)).Bytes;
        TrieNode leaf = new(NodeType.Leaf) { Key = new byte[64], Value = accountRlp };
        TreePath path = TreePath.Empty;
        leaf.ResolveKey(NullTrieNodeResolver.Instance, ref path);
        persistence.Nodes[leaf.Keccak!] = leaf.FullRlp.ToArray();
        persistence.RejectFind = true;
        RecordingVisitor visitor = new() { ExpectAccounts = expectAccounts };
        new NodeRecordBufferVisitor<Context>(buffer, visitor).Accept(leaf.Keccak!, persistence);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistence.Loads.Count, Is.EqualTo(1));
            Assert.That(persistence.StorageAddress, expectAccounts ? Is.EqualTo(address) : Is.Null);
            Assert.That(visitor.Events.Count(e => e.StartsWith("account:")), Is.EqualTo(expectAccounts ? 1 : 0));
            Assert.That(visitor.Events.Any(e => e.Contains("storage")), Is.EqualTo(expectAccounts));
        }
    }

    [Test]
    public async Task Callback_failure_waits_for_other_workers_before_return()
    {
        using NodeRecordBuffer buffer = new(0, 64 * 1024);
        TrieRoot root = Build(buffer, null, true);
        using ManualResetEventSlim started = new();
        using ManualResetEventSlim failed = new();
        using ManualResetEventSlim release = new();
        RecordingVisitor visitor = new()
        {
            OnLeaf = context =>
            {
                if (context.Path.StartsWith("0"))
                {
                    started.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                    Assert.That(buffer.TryLocate(root.Pointer, out NodeRecord borrowed), Is.True);
                    Assert.That(borrowed.Hash, Is.EqualTo(root.Hash));
                }
                else
                {
                    if (!started.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                    failed.Set();
                    throw new InvalidOperationException("visitor failure");
                }
            }
        };
        Task visit = Task.Run(() => new NodeRecordBufferVisitor<Context>(buffer, visitor,
            new VisitingOptions { MaxDegreeOfParallelism = 2 }).Accept(root.ToHash256(), new RecordingResolver { RejectFind = true }));
        try
        {
            Assert.That(failed.Wait(TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(visit.IsCompleted, Is.False, "a worker still owns borrowed buffer records");
            buffer.Dispose();
            release.Set();
            Assert.That(async () => await visit, Throws.TypeOf<InvalidOperationException>().With.Message.EqualTo("visitor failure"));
        }
        finally
        {
            release.Set();
            await visit.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static Hash256 Key(byte first, byte last = 0)
    {
        byte[] bytes = new byte[32];
        bytes[0] = first;
        bytes[^1] = last;
        return new Hash256(bytes);
    }

    private static TrieRoot Build(NodeRecordBuffer buffer, Hash256? address, bool twoBranches)
    {
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(4);
        byte[] largeValue = new byte[40];
        entries.Add(new PatriciaTree.BulkSetEntry(Key(0), largeValue));
        entries.Add(new PatriciaTree.BulkSetEntry(Key(0x10), largeValue));
        if (!twoBranches)
        {
            entries.Add(new PatriciaTree.BulkSetEntry(Key(0, 1), [1]));
            entries.Add(new PatriciaTree.BulkSetEntry(Key(0, 2), [2]));
        }
        return BufferBulkSet.BulkSet(buffer, new EmptySource(), address, TrieRoot.Empty, entries, false);
    }

    private sealed class EmptySource : INodeRecordSource
    {
        public bool TryLoad(in TreePath path, in ValueHash256 hash, out NodeRecord record)
        {
            record = default;
            return false;
        }
    }

    private sealed class RecordingResolver : IScopedTrieStore
    {
        public readonly Dictionary<Hash256, byte[]> Nodes = [];
        public readonly ConcurrentQueue<ReadFlags> Loads = new();
        public bool RejectFind;
        public Hash256? StorageAddress;
        public RecordingResolver() { }
        public RecordingResolver(NodeRecordBuffer buffer, TrieRoot root)
        {
            foreach ((TreePath _, ulong _, NodeRecord record) in buffer.EnumerateNodes(root.Pointer))
                Nodes[new Hash256(record.Hash)] = record.Rlp.ToArray();
        }
        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) =>
            RejectFind ? throw new InvalidOperationException("Snapshot lookup must not be used") : new TrieNode(NodeType.Unknown, hash);
        public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            Loads.Enqueue(flags);
            return Nodes.GetValueOrDefault(hash);
        }
        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address)
        {
            StorageAddress = address;
            return this;
        }
        public INodeStorage.KeyScheme Scheme => INodeStorage.KeyScheme.HalfPath;
        public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) => TryLoadRlp(path, hash, flags);
        public ICommitter BeginCommit(TrieNode? root, WriteFlags flags = WriteFlags.None) => throw new NotSupportedException();
    }

    private readonly record struct Context(string? Value) : INodeContext<Context>
    {
        public string Path => Value ?? "";
        public Context Add(ReadOnlySpan<byte> nibbles)
        {
            string path = Path;
            foreach (byte nibble in nibbles) path += nibble.ToString("x");
            return new Context(path);
        }
        public Context Add(byte nibble) => new(Path + nibble.ToString("x"));
        public Context AddStorage(in ValueHash256 storage) => new(Path + ":storage:");
    }

    private sealed class RecordingVisitor : ITreeVisitor<Context>
    {
        public readonly ConcurrentQueue<string> Events = new();
        public bool IsFullDbScan { get; init; }
        public bool ExpectAccounts { get; init; }
        public ReadFlags ExtraReadFlag { get; init; }
        public bool Prune { get; init; }
        public Action<Context>? OnLeaf { get; init; }
        public bool ShouldVisit(in Context context, in ValueHash256 hash) => !Prune;
        public void VisitTree(in Context context, in ValueHash256 root) => Events.Enqueue("tree");
        public void VisitMissingNode(in Context context, in ValueHash256 hash) => Events.Enqueue("missing:" + context.Path);
        public void VisitBranch(in Context context, TrieNode node) => Record("branch", context, node);
        public void VisitExtension(in Context context, TrieNode node) => Record("extension", context, node);
        public void VisitLeaf(in Context context, TrieNode node)
        {
            Record("leaf", context, node);
            OnLeaf?.Invoke(context);
        }
        public void VisitAccount(in Context context, TrieNode node, in AccountStruct account) => Record("account", context, node);
        private void Record(string kind, Context context, TrieNode node) =>
            Events.Enqueue(kind + ":" + context.Path + ":" + (node.Keccak?.ToString() ?? "inline"));
    }
}
