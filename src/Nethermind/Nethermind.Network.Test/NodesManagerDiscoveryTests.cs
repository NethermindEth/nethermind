// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Logging;
using Nethermind.Network.StaticNodes;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test;

/// <summary>Discovery contract shared by <see cref="StaticNodesManager"/> and <see cref="TrustedNodesManager"/>: every node added to the manager reaches the feed once, with the manager's flag.</summary>
[Parallelizable(ParallelScope.Self)]
public abstract class NodesManagerDiscoveryTests<TManager> where TManager : INodeSource
{
    private const int Sentinel = 254;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    protected abstract TManager CreateManager(string path);
    protected abstract Task InitAsync(TManager manager);
    protected abstract Task<bool> AddAsync(TManager manager, Enode enode);
    protected abstract Task<bool> RemoveAsync(TManager manager, Enode enode);
    protected abstract bool HasFlag(Node node);

    [Test]
    public async Task Nodes_added_during_the_snapshot_are_yielded_once()
    {
        using TempPath file = TempPath.GetTempFile();
        TManager manager = await LoadAsync(file, NodeAt(0), NodeAt(1));
        await using Feed feed = Open(manager);

        List<PublicKey> read = [(await feed.NextAsync()).Id];
        for (int i = 2; i < 34; i++) await AddAsync(manager, NodeAt(i));
        for (int i = 1; i < 34; i++) read.Add((await feed.NextAsync()).Id);

        Assert.That(read, Is.EquivalentTo(Keys(34)), "the file nodes and every node added while they are yielded, each once");
        await AssertOnlySentinelFollowsAsync(manager, feed);
    }

    [Test]
    public async Task Node_added_before_the_enumeration_is_yielded_once()
    {
        using TempPath file = TempPath.GetTempFile();
        TManager manager = await LoadAsync(file);
        await AddAsync(manager, NodeAt(0));
        await using Feed feed = Open(manager);

        Assert.That((await feed.NextAsync()).Id, Is.EqualTo(Key(0)));
        bool addedAgain = await AddAsync(manager, NodeAt(0));
        Assert.That(addedAgain, Is.False, "a repeated add is not an add");
        await AssertOnlySentinelFollowsAsync(manager, feed);
    }

    [Test]
    public async Task Node_removed_and_added_again_is_yielded_again()
    {
        using TempPath file = TempPath.GetTempFile();
        TManager manager = await LoadAsync(file, NodeAt(0));
        await using Feed feed = Open(manager);
        Assert.That((await feed.NextAsync()).Id, Is.EqualTo(Key(0)));

        await RemoveAsync(manager, NodeAt(0));
        await AddAsync(manager, new Enode(Key(0), IPAddress.Loopback, 40404));
        Node readded = await feed.NextAsync();

        Assert.That((readded.Id, readded.Address.Port), Is.EqualTo((Key(0), 40404)), "the node added again after its removal");
        await AssertOnlySentinelFollowsAsync(manager, feed);
    }

    [Test]
    public async Task File_node_removed_before_it_is_yielded_is_not_yielded()
    {
        using TempPath file = TempPath.GetTempFile();
        TManager manager = await LoadAsync(file, NodeAt(0), NodeAt(1), NodeAt(2));
        await using Feed feed = Open(manager);
        PublicKey first = (await feed.NextAsync()).Id;

        for (int i = 0; i < 3; i++)
        {
            if (Key(i) != first) await RemoveAsync(manager, NodeAt(i));
        }

        await AssertOnlySentinelFollowsAsync(manager, feed);
    }

    [Test]
    public async Task File_node_replaced_before_it_is_yielded_comes_out_once_as_the_new_one()
    {
        using TempPath file = TempPath.GetTempFile();
        TManager manager = await LoadAsync(file, NodeAt(0), NodeAt(1), NodeAt(2));
        await using Feed feed = Open(manager);
        PublicKey first = (await feed.NextAsync()).Id;

        List<(PublicKey Id, int Port)> replaced = [];
        for (int i = 0; i < 3; i++)
        {
            if (Key(i) == first) continue;
            await RemoveAsync(manager, NodeAt(i));
            await AddAsync(manager, new Enode(Key(i), IPAddress.Loopback, 40000 + i));
            replaced.Add((Key(i), 40000 + i));
        }

        List<(PublicKey Id, int Port)> read = [];
        for (int i = 0; i < replaced.Count; i++)
        {
            Node node = await feed.NextAsync();
            read.Add((node.Id, node.Address.Port));
        }

        Assert.That(read, Is.EquivalentTo(replaced), "each replaced node once, as the re-added instance");
        await AssertOnlySentinelFollowsAsync(manager, feed);
    }

    [Test]
    public async Task Node_removed_while_its_add_is_queued_is_not_yielded()
    {
        using TempPath file = TempPath.GetTempFile();
        TManager manager = await LoadAsync(file);
        await using Feed feed = Open(manager);
        _ = feed.StartNext();
        await AddAsync(manager, NodeAt(0));
        Assert.That((await feed.NextAsync()).Id, Is.EqualTo(Key(0)));

        // Nobody reads now, so the next add stays queued until it is removed.
        await AddAsync(manager, NodeAt(1));
        await RemoveAsync(manager, NodeAt(1));

        await AssertOnlySentinelFollowsAsync(manager, feed);
    }

    [Test]
    public async Task Node_replaced_while_its_add_is_queued_comes_out_once_as_the_new_one()
    {
        using TempPath file = TempPath.GetTempFile();
        TManager manager = await LoadAsync(file);
        await using Feed feed = Open(manager);
        _ = feed.StartNext();
        await AddAsync(manager, NodeAt(0));
        Assert.That((await feed.NextAsync()).Id, Is.EqualTo(Key(0)));

        // Nobody reads now: the add, its removal and the add on a new port are all queued.
        await AddAsync(manager, NodeAt(1));
        await RemoveAsync(manager, NodeAt(1));
        await AddAsync(manager, new Enode(Key(1), IPAddress.Loopback, 40001));
        Node readded = await feed.NextAsync();

        Assert.That((readded.Id, readded.Address.Port), Is.EqualTo((Key(1), 40001)), "only the re-added instance");
        await AssertOnlySentinelFollowsAsync(manager, feed);
    }

    [Test]
    public async Task Two_enumerations_each_get_every_node_and_outlive_each_other()
    {
        using TempPath file = TempPath.GetTempFile();
        TManager manager = await LoadAsync(file, NodeAt(0), NodeAt(1));
        await using Feed b = Open(manager);
        await using (Feed a = Open(manager))
        {
            List<PublicKey> readA = [(await a.NextAsync()).Id]; // a is inside its snapshot
            await AddAsync(manager, NodeAt(2));
            List<PublicKey> readB = [];
            for (int i = 0; i < 3; i++) readB.Add((await b.NextAsync()).Id); // b starts later: all three are in its copy
            await AddAsync(manager, NodeAt(3));
            for (int i = 0; i < 3; i++) readA.Add((await a.NextAsync()).Id);
            readB.Add((await b.NextAsync()).Id);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(readA, Is.EquivalentTo(Keys(4)), "the first enumeration");
                Assert.That(readB, Is.EquivalentTo(Keys(4)), "the second enumeration");
            }

            _ = a.StartNext();
            await AssertOnlySentinelFollowsAsync(manager, b);
            Assert.That((await a.NextAsync()).Id, Is.EqualTo(Key(Sentinel)), "no other node is queued for the first enumeration");
        }

        // The first enumeration has ended; the second still gets adds.
        _ = b.StartNext();
        await AddAsync(manager, NodeAt(Sentinel - 1));
        Assert.That((await b.NextAsync()).Id, Is.EqualTo(Key(Sentinel - 1)), "the second enumeration outlives the first");
    }

    [Test]
    public async Task Adds_are_not_dropped_while_the_consumer_is_paused()
    {
        using TempPath file = TempPath.GetTempFile();
        TManager manager = await LoadAsync(file);
        await using Feed feed = Open(manager);
        _ = feed.StartNext(); // the feed is past its (empty) snapshot and waits for adds

        for (int i = 0; i < 200; i++) await AddAsync(manager, NodeAt(i));
        List<PublicKey> read = [];
        for (int i = 0; i < 200; i++) read.Add((await feed.NextAsync()).Id);

        Assert.That(read, Is.EquivalentTo(Keys(200)), "every add queued while nobody reads");
        await AssertOnlySentinelFollowsAsync(manager, feed);
    }

    // The read starts before the add, so the sentinel can only come from the add feed, and nothing may come before it.
    private async Task AssertOnlySentinelFollowsAsync(TManager manager, Feed feed)
    {
        _ = feed.StartNext();
        await AddAsync(manager, NodeAt(Sentinel));
        Assert.That((await feed.NextAsync()).Id, Is.EqualTo(Key(Sentinel)), "no other node is queued before a later add");
    }

    private async Task<TManager> LoadAsync(TempPath file, params Enode[] nodes)
    {
        await File.WriteAllTextAsync(file.Path, $"[{string.Join(',', nodes.Select(static n => $"\"{n}\""))}]");
        TManager manager = CreateManager(file.Path);
        await InitAsync(manager);
        return manager;
    }

    private Feed Open(TManager manager) => new(manager, HasFlag);

    private static PublicKey Key(int i) => TestItem.PublicKeys[i];

    private static IEnumerable<PublicKey> Keys(int count) => Enumerable.Range(0, count).Select(Key);

    private static Enode NodeAt(int i) => new(Key(i), IPAddress.Loopback, 30303 + i);

    /// <summary>One enumeration of a manager's feed: every read is bounded and checks the manager's flag; disposal cancels a pending read before disposing.</summary>
    private sealed class Feed(INodeSource source, Func<Node, bool> hasFlag) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private IAsyncEnumerator<Node>? _nodes;
        private Task<bool>? _next;
        private int _read;

        public Task<bool> StartNext()
        {
            _nodes ??= source.DiscoverNodes(_cts.Token).GetAsyncEnumerator();
            return _next ??= _nodes.MoveNextAsync().AsTask();
        }

        public async Task<Node> NextAsync()
        {
            Task<bool> next = StartNext();
            try
            {
                Assert.That(await next.WaitAsync(ReadTimeout), Is.True, "the feed ended");
            }
            catch (TimeoutException)
            {
                Assert.Fail($"no node arrived within {ReadTimeout.TotalSeconds} s after {_read} nodes");
            }

            _next = null;
            _read++;
            Node node = _nodes!.Current;
            Assert.That(hasFlag(node), Is.True, "every node of the feed carries the manager's flag");
            return node;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            if (_next is not null)
            {
                try
                {
                    await _next;
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (_nodes is not null) await _nodes.DisposeAsync();
            _cts.Dispose();
        }
    }
}

[TestFixture]
public class StaticNodesManagerDiscoveryTests : NodesManagerDiscoveryTests<StaticNodesManager>
{
    protected override StaticNodesManager CreateManager(string path) => new(path, LimboLogs.Instance);
    protected override Task InitAsync(StaticNodesManager manager) => manager.InitAsync();
    protected override Task<bool> AddAsync(StaticNodesManager manager, Enode enode) => manager.AddAsync(new NetworkNode(enode), updateFile: false);
    protected override Task<bool> RemoveAsync(StaticNodesManager manager, Enode enode) => manager.RemoveAsync(new NetworkNode(enode), updateFile: false);
    protected override bool HasFlag(Node node) => node.IsStatic;
}

[TestFixture]
public class TrustedNodesManagerDiscoveryTests : NodesManagerDiscoveryTests<TrustedNodesManager>
{
    protected override TrustedNodesManager CreateManager(string path) => new(path, LimboLogs.Instance);
    protected override Task InitAsync(TrustedNodesManager manager) => manager.InitAsync();
    protected override Task<bool> AddAsync(TrustedNodesManager manager, Enode enode) => manager.AddAsync(enode, updateFile: false);
    protected override Task<bool> RemoveAsync(TrustedNodesManager manager, Enode enode) => manager.RemoveAsync(enode, updateFile: false);
    protected override bool HasFlag(Node node) => node.IsTrusted;
}
