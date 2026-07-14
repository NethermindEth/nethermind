// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv4.Kademlia;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test.Discv4.Kademlia
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NodeSourceTests
    {
        private const uint CompatibleForkHash = 0x11111111;
        private const uint IncompatibleForkHash = 0x22222222;

        private TestKademliaDiscovery _kademliaDiscovery = null!;
        private IKademliaAdapter _adapter = null!;
        private NodeSource _nodeSource = null!;
        private DiscoveryConfig _discoveryConfig = null!;
        private KademliaConfig<Node> _kademliaConfig = null!;
        private IForkInfo _forkInfo = null!;
        private Channel<Node> _discoveredNodes = null!;

        [SetUp]
        public void Setup()
        {
            _kademliaDiscovery = new();
            _adapter = Substitute.For<IKademliaAdapter>();
            _discoveredNodes = Channel.CreateUnbounded<Node>();
            _adapter.ReadDiscoveredNodes(Arg.Any<CancellationToken>())
                .Returns(call => _discoveredNodes.Reader.ReadAllAsync(call.Arg<CancellationToken>()));
            _discoveryConfig = new DiscoveryConfig { ConcurrentDiscoveryJob = 2 };
            _kademliaConfig = new KademliaConfig<Node>
            {
                CurrentNodeId = new Node(TestItem.PublicKeyD, "127.0.0.1", 30303),
                KSize = 1
            };
            _forkInfo = Substitute.For<IForkInfo>();
            _forkInfo.IsForkIdCompatible(Arg.Any<ForkId>())
                .Returns(static call => call.Arg<ForkId>().ForkHash == CompatibleForkHash);
            _nodeSource = new NodeSource(
                _kademliaDiscovery,
                _adapter,
                _discoveryConfig,
                _kademliaConfig,
                _forkInfo,
                LimboLogs.Instance);
        }

        [TearDown]
        public async Task TearDown() => await _adapter.DisposeAsync();

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_drive_kademlia_discovery_and_forward_buffered_nodes(CancellationToken token)
        {
            Node node = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            RaiseDiscoveredNode(node);

            await using IAsyncEnumerator<Node> enumerator = _nodeSource.DiscoverNodes(token).GetAsyncEnumerator(token);

            Assert.That(await enumerator.MoveNextAsync(), Is.True);
            Assert.That(enumerator.Current, Is.EqualTo(node));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_kademliaDiscovery.DiscoverNodesCalls, Is.EqualTo(1));
                Assert.That(_kademliaDiscovery.ConcurrentDiscoveryJobs, Is.EqualTo(_discoveryConfig.ConcurrentDiscoveryJob));
            }
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_not_forward_raw_kademlia_contacts(CancellationToken token)
        {
            Node rawContact = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            Node peerNode = new(TestItem.PublicKeyB, "192.168.1.2", 30303);
            TaskCompletionSource rawContactObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _kademliaDiscovery.DiscoverNodesHandler = (_, _, _) => YieldAndSignal(rawContact, rawContactObserved);

            await using IAsyncEnumerator<Node> enumerator = _nodeSource.DiscoverNodes(token).GetAsyncEnumerator(token);
            ValueTask<bool> moveNext = enumerator.MoveNextAsync();
            await rawContactObserved.Task.WaitAsync(token);

            Assert.That(moveNext.IsCompleted, Is.False);
            RaiseDiscoveredNode(peerNode);

            Assert.That(await moveNext.AsTask(), Is.True);
            Assert.That(enumerator.Current, Is.EqualTo(peerNode));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_skip_self_and_incompatible_fork_nodes(CancellationToken token)
        {
            Node self = new(TestItem.PublicKeyD, "192.168.1.4", 30303);
            Node incompatible = CreateNodeWithForkId(TestItem.PrivateKeys[0], "192.168.1.1", IncompatibleForkHash);
            Node compatible = CreateNodeWithForkId(TestItem.PrivateKeys[1], "192.168.1.2", CompatibleForkHash);

            await using IAsyncEnumerator<Node> enumerator = _nodeSource.DiscoverNodes(token).GetAsyncEnumerator(token);
            ValueTask<bool> moveNext = enumerator.MoveNextAsync();
            await _kademliaDiscovery.Started.Task.WaitAsync(token);
            RaiseDiscoveredNode(self);
            RaiseDiscoveredNode(incompatible);
            RaiseDiscoveredNode(compatible);

            Assert.That(await moveNext.AsTask(), Is.True);
            Assert.That(enumerator.Current.Id, Is.EqualTo(compatible.Id));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_keep_node_when_fork_check_fails(CancellationToken token)
        {
            Node node = CreateNodeWithForkId(TestItem.PrivateKeys[0], "192.168.1.1", IncompatibleForkHash);
            _forkInfo.IsForkIdCompatible(Arg.Any<ForkId>()).Throws(new InvalidOperationException("Fork info unavailable"));

            await using IAsyncEnumerator<Node> enumerator = _nodeSource.DiscoverNodes(token).GetAsyncEnumerator(token);
            ValueTask<bool> moveNext = enumerator.MoveNextAsync();
            await _kademliaDiscovery.Started.Task.WaitAsync(token);
            RaiseDiscoveredNode(node);

            Assert.That(await moveNext.AsTask(), Is.True);
            Assert.That(enumerator.Current.Id, Is.EqualTo(node.Id));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_forward_reauthenticated_endpoint_reversion(CancellationToken token)
        {
            Node endpointA = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            Node endpointB = new(TestItem.PublicKeyA, "192.168.1.1", 30304);

            await using IAsyncEnumerator<Node> enumerator = _nodeSource.DiscoverNodes(token).GetAsyncEnumerator(token);
            ValueTask<bool> firstMove = enumerator.MoveNextAsync();
            await _kademliaDiscovery.Started.Task.WaitAsync(token);
            RaiseDiscoveredNode(endpointA);
            Assert.That(await firstMove.AsTask(), Is.True);
            Assert.That(enumerator.Current.Port, Is.EqualTo(30303));

            ValueTask<bool> secondMove = enumerator.MoveNextAsync();
            RaiseDiscoveredNode(endpointB);
            Assert.That(await secondMove.AsTask(), Is.True);
            Assert.That(enumerator.Current.Port, Is.EqualTo(30304));

            ValueTask<bool> revertedMove = enumerator.MoveNextAsync();
            RaiseDiscoveredNode(endpointA);
            Assert.That(await revertedMove.AsTask(), Is.True);
            Assert.That(enumerator.Current.Port, Is.EqualTo(30303));
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_forward_higher_enr_endpoint_revision(CancellationToken token)
        {
            Node initialNode = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            NodeRecord upgradedRecord = TestEnrBuilder.BuildSigned(
                TestItem.PrivateKeyA,
                IPAddress.Parse("192.168.1.1"),
                tcpPort: 30304,
                udpPort: 30303,
                enrSequence: 2);
            Assert.That(Node.TryFromEnr(upgradedRecord, out Node? upgradedNode), Is.True);

            await using IAsyncEnumerator<Node> enumerator = _nodeSource.DiscoverNodes(token).GetAsyncEnumerator(token);
            ValueTask<bool> firstMove = enumerator.MoveNextAsync();
            await _kademliaDiscovery.Started.Task.WaitAsync(token);
            RaiseDiscoveredNode(initialNode);
            Assert.That(await firstMove.AsTask(), Is.True);
            Assert.That(enumerator.Current.Port, Is.EqualTo(30303));

            ValueTask<bool> upgradeMove = enumerator.MoveNextAsync();
            RaiseDiscoveredNode(upgradedNode!);

            Assert.That(await upgradeMove.AsTask(), Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(enumerator.Current.Id, Is.EqualTo(initialNode.Id));
                Assert.That(enumerator.Current.Port, Is.EqualTo(30304));
                Assert.That(enumerator.Current.Enr.EnrSequence, Is.EqualTo(2));
            }
        }

        [Test]
        [CancelAfter(10000)]
        public async Task DiscoverNodes_should_stop_random_walk_when_enumeration_is_disposed(CancellationToken token)
        {
            Node node = new(TestItem.PublicKeyA, "192.168.1.1", 30303);
            TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _kademliaDiscovery.DiscoverNodesHandler = (_, _, discoveryToken) => WaitForCancellation(stopped, discoveryToken);

            ValueTask<List<Node>> nodesTask = _nodeSource.DiscoverNodes(token).Take(1).ToListAsync(token);
            await _kademliaDiscovery.Started.Task.WaitAsync(token);
            RaiseDiscoveredNode(node);

            Assert.That(await nodesTask, Has.Count.EqualTo(1));
            await stopped.Task.WaitAsync(token);
        }

        private void RaiseDiscoveredNode(Node node)
            => Assert.That(_discoveredNodes.Writer.TryWrite(node), Is.True);

        private static async IAsyncEnumerable<T> YieldAndSignal<T>(
            T item,
            TaskCompletionSource signal)
        {
            yield return item;
            signal.TrySetResult();
            await Task.Yield();
        }

        private static async IAsyncEnumerable<Node> WaitForCancellation(
            TaskCompletionSource stopped,
            [EnumeratorCancellation] CancellationToken token)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                stopped.TrySetResult();
            }

            yield break;
        }

        private static Node CreateNodeWithForkId(PrivateKey privateKey, string host, uint forkHash) =>
            new(privateKey.PublicKey, host, 30303)
            {
                Enr = TestEnrBuilder.BuildSigned(
                    privateKey,
                    IPAddress.Parse(host),
                    configureExtras: enr => enr.SetEntry(new EthEntry(new ForkId(forkHash, 0).HashBytes, 0)))
            };

        private sealed class TestKademliaDiscovery : IKademliaDiscovery<PublicKey, Node>
        {
            public int DiscoverNodesCalls { get; private set; }

            public int ConcurrentDiscoveryJobs { get; private set; }

            public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Func<int, int, CancellationToken, IAsyncEnumerable<Node>> DiscoverNodesHandler { private get; set; } =
                (_, _, _) => AsyncEnumerable.Empty<Node>();

            public IAsyncEnumerable<Node> DiscoverNodes(int concurrentDiscoveryJobs, int lookupResultLimit, CancellationToken token)
            {
                DiscoverNodesCalls++;
                ConcurrentDiscoveryJobs = concurrentDiscoveryJobs;
                Started.TrySetResult();
                return DiscoverNodesHandler(concurrentDiscoveryJobs, lookupResultLimit, token);
            }
        }
    }
}
