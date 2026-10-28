// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Store.Test;

/// <summary>A write after a parallel <see cref="PatriciaTree.BulkSet"/> whose job threw, on a thread that ran that job.</summary>
public class PatriciaTreeBulkSetFailureTests
{
    /// <summary>The depth from which the failing store has no nodes: each job's first write walks one level below
    /// its depth-2 node and throws there, one frame deep.</summary>
    private const int MissingDepth = 3;

    [Test]
    public void Write_after_a_failed_parallel_bulk_set_on_the_same_thread_is_applied([Values(INodeStorage.KeyScheme.Hash, INodeStorage.KeyScheme.HalfPath)] INodeStorage.KeyScheme scheme)
    {
        if (Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("Requires parallel trie work.");

        using MemDb db = new();
        (Hash256 root, List<(Hash256 key, byte[] value)> updates) = OnNewThread(() => CreatePrefixedTree(db, scheme));
        (Hash256 key, byte[] value) write = updates[0];
        Hash256 expected = OnNewThread(() => SetOneByOne(CreateTree(db, scheme, root), [write]));

        (Exception? failure, Hash256 actual) = OnNewThread(() =>
        {
            Exception? failed;
            // A budget of one runs every job on this thread, so the job that throws hands its stack back here.
            using (ParallelUnbalancedWork.BeginWorkerScope(1))
            {
                failed = TryBulkSet(CreateTree(db, scheme, root, missingDepth: MissingDepth), updates);
            }

            return (failed, SetOneByOne(CreateTree(db, scheme, root), [write]));
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure, Is.InstanceOf<MissingTrieNodeException>());
            Assert.That(actual, Is.EqualTo(expected));
        }
    }

    [Test]
    public void Parallel_bulk_set_on_a_worker_that_ran_a_failed_job_computes_the_right_root([Values(INodeStorage.KeyScheme.Hash, INodeStorage.KeyScheme.HalfPath)] INodeStorage.KeyScheme scheme)
    {
        if (Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("Requires parallel trie work.");

        using MemDb db = new();
        (Hash256 root, List<(Hash256 key, byte[] value)> updates) = OnNewThread(() => CreatePrefixedTree(db, scheme));
        Hash256 expected = OnNewThread(() => SetOneByOne(CreateTree(db, scheme, root), updates));

        using Rendezvous failingJobs = new();
        using Rendezvous laterJobs = new();
        using BlockingCollection<IThreadPoolWorkItem> runners = new();
        // The scope's only runner besides the caller: both bulk sets spread their jobs over these two threads.
        Thread worker = new(() =>
        {
            foreach (IThreadPoolWorkItem runner in runners.GetConsumingEnumerable()) runner.Execute();
        })
        { IsBackground = true };
        worker.Start();

        (Exception? failure, Exception? laterFailure, Hash256 actual) = OnNewThread(() =>
        {
            using (new ParallelUnbalancedWork.WorkerScope(2, runners.Add))
            {
                // Each thread's job reaches a missing node before either throws, so both threads end up with a failed job.
                PatriciaTree failing = CreateTree(db, scheme, root, missingDepth: MissingDepth,
                    onLoad: path => { if (path.Length >= MissingDepth) failingJobs.Arrive(); });
                Exception? failed = TryBulkSet(failing, updates);

                // An unrelated tree on the same threads; each of them takes a job before either goes on.
                PatriciaTree later = CreateTree(db, scheme, root, onLoad: path => { if (path.Length >= 1) laterJobs.Arrive(); });
                Exception? laterFailed = TryBulkSet(later, updates);
                if (laterFailed is null) later.UpdateRootHash();
                return (failed, laterFailed, later.RootHash);
            }
        });

        runners.CompleteAdding();
        worker.Join();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure, Is.InstanceOf<MissingTrieNodeException>());
            Assert.That(failingJobs.Met, Is.EqualTo(2), "Both threads ran a job that hit the missing node.");
            Assert.That(laterJobs.Met, Is.EqualTo(2), "Both threads ran a job of the later bulk set.");
            Assert.That(laterFailure, Is.Null);
            Assert.That(actual, Is.EqualTo(expected));
        }
    }

    /// <summary>
    /// A committed tree with 16 keys under each two-nibble prefix, and an update of one key under each prefix whose
    /// second nibble is below 9: 144 entries over all 16 first nibbles, so a bulk set of them runs in parallel, and one
    /// entry per depth-2 node, so each job writes it with a single walk from that node.
    /// </summary>
    private static (Hash256 root, List<(Hash256 key, byte[] value)> updates) CreatePrefixedTree(MemDb db, INodeStorage.KeyScheme scheme)
    {
        Random rng = new(1);
        PatriciaTree tree = CreateTree(db, scheme, Keccak.EmptyTreeHash);
        List<(Hash256 key, byte[] value)> updates = [];
        for (int prefix = 0; prefix < 256; prefix++)
        {
            for (int i = 0; i < 16; i++)
            {
                byte[] key = new byte[32];
                rng.NextBytes(key);
                key[0] = (byte)prefix;
                tree.Set(key, RandomValue(rng));
                if (i == 0 && (prefix & 0xf) < 9) updates.Add((new Hash256(key), RandomValue(rng)));
            }
        }

        tree.Commit();
        return (tree.RootHash, updates);
    }

    private static byte[] RandomValue(Random rng)
    {
        byte[] value = new byte[32];
        rng.NextBytes(value);
        return value;
    }

    private static PatriciaTree CreateTree(MemDb db, INodeStorage.KeyScheme scheme, Hash256 root, int missingDepth = int.MaxValue, Action<TreePath>? onLoad = null) =>
        new(new FailingTrieStore(new RawScopedTrieStore(new NodeStorage(db, scheme)), missingDepth, onLoad), root, true, LimboLogs.Instance);

    private static Hash256 SetOneByOne(PatriciaTree tree, List<(Hash256 key, byte[] value)> items)
    {
        foreach ((Hash256 key, byte[] value) in items) tree.Set(key.Bytes, value);
        tree.UpdateRootHash();
        return tree.RootHash;
    }

    private static Exception? TryBulkSet(PatriciaTree tree, List<(Hash256 key, byte[] value)> items)
    {
        using ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(items.Count);
        foreach ((Hash256 key, byte[] value) in items) entries.Add(new PatriciaTree.BulkSetEntry(key, value));
        try
        {
            tree.BulkSet(entries);
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>Runs <paramref name="func"/> on a thread of its own, which no earlier trie write has used.</summary>
    private static T OnNewThread<T>(Func<T> func)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
        });
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }

    /// <summary>Holds each thread at its first arrival until a second thread arrives.</summary>
    private sealed class Rendezvous : IDisposable
    {
        private readonly Barrier _barrier = new(2);
        private readonly ThreadLocal<bool> _arrived = new();
        private int _met;

        /// <summary>How many threads went on with another thread waiting at the same time.</summary>
        public int Met => Volatile.Read(ref _met);

        public void Arrive()
        {
            if (_arrived.Value) return;
            _arrived.Value = true;
            if (_barrier.SignalAndWait(TimeSpan.FromSeconds(10))) Interlocked.Increment(ref _met);
        }

        public void Dispose()
        {
            _barrier.Dispose();
            _arrived.Dispose();
        }
    }

    /// <summary>Has no node at <paramref name="missingDepth"/> or deeper, as a pruned or partly written store, and calls
    /// <paramref name="onLoad"/> before each load.</summary>
    private sealed class FailingTrieStore(IScopedTrieStore baseTrieStore, int missingDepth, Action<TreePath>? onLoad) : IScopedTrieStore
    {
        public TrieNode FindCachedOrUnknown(in TreePath path, Hash256 hash) => baseTrieStore.FindCachedOrUnknown(in path, hash);

        public byte[]? LoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None)
        {
            onLoad?.Invoke(path);
            return path.Length < missingDepth
                ? baseTrieStore.LoadRlp(in path, hash, flags)
                : throw new MissingTrieNodeException($"Node {hash} at {path} is missing.", null, path, hash);
        }

        public byte[]? TryLoadRlp(in TreePath path, Hash256 hash, ReadFlags flags = ReadFlags.None) =>
            path.Length < missingDepth ? baseTrieStore.TryLoadRlp(in path, hash, flags) : null;

        public ITrieNodeResolver GetStorageTrieNodeResolver(Hash256? address) => baseTrieStore.GetStorageTrieNodeResolver(address);

        public INodeStorage.KeyScheme Scheme => baseTrieStore.Scheme;

        public ICommitter BeginCommit(TrieNode? root, WriteFlags writeFlags = WriteFlags.None) => baseTrieStore.BeginCommit(root, writeFlags);
    }
}
