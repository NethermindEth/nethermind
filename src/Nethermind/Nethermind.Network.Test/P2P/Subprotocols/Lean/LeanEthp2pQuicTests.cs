// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Lean.LeanTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

/// <summary>Two in-process nodes over real QUIC on loopback: authentication, stream limits, retrieval of each kind and broadcast.</summary>
/// <remarks>Skipped where System.Net.Quic is unsupported, such as macOS or Linux without libmsquic.</remarks>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
[NonParallelizable]
public class LeanEthp2pQuicTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [SetUp]
    public void RequireQuic()
    {
        if (!LeanEthp2pHost.IsSupported) Assert.Ignore("QUIC is unavailable on this platform (libmsquic is not installed)");
    }

    internal sealed class QuicNode : IAsyncDisposable
    {
        private QuicNode(PrivateKey key, TestBroadcastProfile? profile, Action<NetworkConfig>? configure)
        {
            Key = key;
            Node = new LeanTestNode(manualTime: false);
            Config = new NetworkConfig { LocalIp = "127.0.0.1", LeanEthp2pPort = 0, LeanBindings = LeanBinding.Ethp2p, LeanCommonBinding = LeanBinding.Ethp2p };
            configure?.Invoke(Config);
            Host = new LeanEthp2pHost(Node.Transport, Config, new InsecureProtectedPrivateKey(key), LimboLogs.Instance, Node.EthTransactions, profile);
        }

        public PrivateKey Key { get; }
        public LeanTestNode Node { get; }
        public NetworkConfig Config { get; }
        public LeanEthp2pHost Host { get; }
        public LeanEthp2pRecord Record => new(Key.PublicKey, Host.LocalEndPoint!);

        public static async Task<QuicNode> Start(PrivateKey key, TestBroadcastProfile? profile = null, Action<NetworkConfig>? configure = null)
        {
            QuicNode node = new(key, profile, configure);
            await node.Host.StartAsync(CancellationToken.None);
            return node;
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Node.Dispose();
        }
    }

    private static async Task Until(Func<bool> condition, string? because = null)
    {
        using CancellationTokenSource timeout = new(Timeout);
        try
        {
            while (!condition()) await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Assert.Fail($"timed out waiting{(because is null ? "" : $" for {because}")}");
        }
    }

    internal static async Task<(QuicNode A, QuicNode B)> Connected(TestBroadcastProfile? profile = null)
    {
        QuicNode a = await QuicNode.Start(TestItem.PrivateKeyA, profile is null ? null : new TestBroadcastProfile());
        QuicNode b = await QuicNode.Start(TestItem.PrivateKeyB, profile);
        RegisterRecovery(a, b);
        RegisterRecovery(b, a);
        Assert.That(await a.Host.DialAsync(b.Record, CancellationToken.None), Is.Not.Null);
        await Until(() => a.Node.Transport.PeerCount == 1 && b.Node.Transport.PeerCount == 1, "Status in both directions");
        return (a, b);
    }

    private static void RegisterRecovery(QuicNode receiver, QuicNode source) =>
        receiver.Node.EthTransactions.Register(source.Key.PublicKey, source, (hashes, reserve, token) =>
        {
            List<Transaction> transactions = [];
            foreach (ValueHash256 hash in hashes)
            {
                token.ThrowIfCancellationRequested();
                source.Node.EthTransactions.TryGetRetained(hash, out Transaction? transaction);
                transaction ??= source.Node.Pending.FirstOrDefault(t => t.Hash!.ValueHash256 == hash);
                if (transaction is not null && reserve(Envelope(transaction).Length)) transactions.Add(transaction);
            }
            return Task.FromResult(transactions.ToArray());
        });

    private static void AssertNoViolations(QuicNode a, QuicNode b)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Node.Transport.PeerCount, Is.EqualTo(1), "a kept its peer");
            Assert.That(b.Node.Transport.PeerCount, Is.EqualTo(1), "b kept its peer");
        }
    }

    [Test]
    public async Task Mempool_wrapper_spanning_several_response_streams_is_fetched_validated_and_admitted()
    {
        (QuicNode a, QuicNode b) = await Connected();
        await using (a)
        await using (b)
        {
            Transaction transaction = FrameTransaction(21);
            // Five MiB need three concurrent GetChunks requests, each on its own response stream.
            byte[] wrapper = Wrapper(5 * 1024 * 1024, false, transaction);
            LeanDescriptor descriptor = Describe(wrapper);

            Assert.That((await a.Node.Wrappers.AcceptDetailedAsync(wrapper,
                new Dictionary<ValueHash256, Transaction> { [transaction.Hash!.ValueHash256] = transaction })).HasValidProof, Is.True);

            await Until(() => b.Node.Transport.IsStored(descriptor.ObjectId) && b.Node.Pending.Any(t => t.Hash == transaction.Hash),
                "the wrapper and its admitted transaction at b");
            AssertNoViolations(a, b);
        }
    }

    [Test]
    public async Task Hash_entries_are_resolved_through_the_separate_authenticated_eth_binding()
    {
        (QuicNode a, QuicNode b) = await Connected();
        await using (a)
        await using (b)
        {
            Transaction transaction = FrameTransaction(22);
            a.Node.Pending.Add(transaction);
            byte[] wrapper = Wrapper(LeanProtocol.ChunkBytes, true, transaction);

            Assert.That((await a.Node.Wrappers.AcceptDetailedAsync(wrapper,
                new Dictionary<ValueHash256, Transaction> { [transaction.Hash!.ValueHash256] = transaction })).HasValidProof, Is.True);

            await Until(() => b.Node.Pending.Any(t => t.Hash == transaction.Hash), "the resolved transaction at b");
            AssertNoViolations(a, b);
        }
    }

    [Test]
    public async Task A_quic_peer_without_compatible_eth_recovery_does_not_enable_kind_one()
    {
        await using QuicNode a = await QuicNode.Start(TestItem.PrivateKeyA);
        await using QuicNode b = await QuicNode.Start(TestItem.PrivateKeyB);
        Assert.That(await a.Host.DialAsync(b.Record, CancellationToken.None), Is.Not.Null);
        await Until(() => a.Host.ConnectionCount == 0 && b.Host.ConnectionCount == 0, "incompatible Status closes this binding");
        Assert.That(a.Node.Transport.PeerCount + b.Node.Transport.PeerCount, Is.Zero);
    }

    [Test]
    public async Task Block_proof_retrieval_checks_the_existing_ordinary_header_commitment()
    {
        (QuicNode a, QuicNode b) = await Connected();
        await using (a)
        await using (b)
        {
            byte[] proofBytes = Proof(300_000);
            BlockHeader header = LeanTransportTests.ProofHeader(proofBytes);
            IBlockTree tree = a.Node.Tree;
            tree.IsMainChain(header).Returns(true);
            tree.FindHeader(header.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(header);
            tree.FindBlock(header.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(new Block(header, new BlockBody()) { StarkProof = proofBytes });
            b.Node.Tree.FindHeader(header.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(header);

            using CancellationTokenSource deadline = new(Timeout);
            RecursiveStark? proof = await b.Node.Transport.TryGetAsync(new Block(header, new BlockBody()), deadline.Token);

            Assert.That(proof?.StarkProof, Is.EqualTo(proofBytes));
            AssertNoViolations(a, b);
        }
    }

    [Test]
    public async Task Block_proof_is_broadcast_as_authenticated_shards_bound_to_the_known_header()
    {
        (QuicNode a, QuicNode b) = await Connected(new TestBroadcastProfile());
        await using (a)
        await using (b)
        {
            byte[] proofBytes = Proof(300_000);
            BlockHeader header = LeanTransportTests.ProofHeader(proofBytes);
            Block block = new(header, new BlockBody()) { StarkProof = proofBytes };
            a.Node.Tree.FindHeader(header.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(header);
            b.Node.Tree.FindHeader(header.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(header);
            byte[] body = LeanBodies.EncodeBlockProof(proofBytes);
            LeanDescriptor descriptor = LeanDescriptor.Create(LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile,
                LeanDescriptor.BlockProofContext(header.Hash!.ValueHash256), body);
            a.Node.Tree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(block));
            await Until(() => a.Node.Transport.IsStored(descriptor.ObjectId), "the validated block proof at a");
            LeanBroadcastManifest manifest = LeanBroadcastManifest.Create(TestBroadcastProfile.ProfileId, [1, 2, 3, 4], 10, 0, 7, default,
                descriptor, body, out _);

            Assert.That(a.Host.Broadcast!.Originate(manifest, TestBroadcastProfile.Sign(manifest), []), Is.True);
            await Until(() => b.Node.Transport.HasBroadcastSidecar(header.Hash.ValueHash256), "the reconstructed proof at b");
            RecursiveStark? proof = await b.Node.Transport.TryGetAsync(new Block(header, new BlockBody()), CancellationToken.None);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(proof?.StarkProof, Is.EqualTo(proofBytes));
                Assert.That(b.Host.Broadcast!.SessionCount, Is.EqualTo(1));
                Assert.That(b.Host.Broadcast.ShardsAccepted, Is.GreaterThanOrEqualTo(LeanReedSolomon.DataShards));
            }
            AssertNoViolations(a, b);
        }
    }

    [Test]
    public async Task Dial_rejects_a_peer_whose_tls_identity_is_not_the_record_key()
    {
        await using QuicNode a = await QuicNode.Start(TestItem.PrivateKeyA);
        await using QuicNode b = await QuicNode.Start(TestItem.PrivateKeyB);

        LeanEthp2pConnection? connection = await a.Host.DialAsync(new LeanEthp2pRecord(TestItem.PrivateKeyC.PublicKey, b.Host.LocalEndPoint!),
            CancellationToken.None);

        await Task.Delay(300);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(connection, Is.Null);
            Assert.That(a.Host.ConnectionCount + b.Host.ConnectionCount, Is.Zero);
            Assert.That(b.Node.Transport.PeerCount, Is.Zero);
        }
    }

    private static QuicClientConnectionOptions RawClient(IPEndPoint server, string alpn, X509Certificate2? certificate) => new()
    {
        RemoteEndPoint = server,
        DefaultStreamErrorCode = 1,
        DefaultCloseErrorCode = 0,
        MaxInboundUnidirectionalStreams = 8,
        ClientAuthenticationOptions = new SslClientAuthenticationOptions
        {
            ApplicationProtocols = [new SslApplicationProtocol(alpn)],
            TargetHost = LeanEthp2pProtocol.ServerName,
            ClientCertificates = certificate is null ? null : [certificate],
            RemoteCertificateValidationCallback = static (_, _, _, _) => true
        }
    };

    public enum ClientCertificate { Libp2p, None, MislabelledEc, MislabelledRsa }

    [TestCase("libp2p", ClientCertificate.Libp2p, TestName = "Wrong ALPN is refused in the handshake")]
    [TestCase(LeanEthp2pProtocol.Alpn, ClientCertificate.None, TestName = "Client without a libp2p certificate is never authenticated")]
    [TestCase(LeanEthp2pProtocol.Alpn, ClientCertificate.MislabelledEc, TestName = "Ed25519 signature algorithm over a P-256 client key is refused")]
    [TestCase(LeanEthp2pProtocol.Alpn, ClientCertificate.MislabelledRsa, TestName = "ECDSA signature algorithm over an RSA client key is refused")]
    public async Task Unauthenticated_client_never_reaches_the_transport(string alpn, ClientCertificate kind)
    {
        await using QuicNode b = await QuicNode.Start(TestItem.PrivateKeyB);
        using X509Certificate2? certificate = kind switch
        {
            ClientCertificate.Libp2p => LeanEthp2pIdentity.CreateCertificate(TestItem.PrivateKeyA),
            ClientCertificate.None => null,
            _ => LeanEthp2pTests.MislabelledCertificate(rsaKey: kind == ClientCertificate.MislabelledRsa)
        };
        try
        {
            await using QuicConnection client = await QuicConnection.ConnectAsync(RawClient(b.Host.LocalEndPoint!, alpn, certificate));
            // TLS 1.3 clients finish before the server checks their certificate; the server then closes without processing data.
            await using QuicStream stream = await client.OpenOutboundStreamAsync(QuicStreamType.Unidirectional);
            await stream.WriteAsync(new byte[] { LeanEthp2pProtocol.ControlStream });
        }
        catch (Exception exception) when (exception is QuicException or System.Security.Authentication.AuthenticationException) { }

        await Task.Delay(300);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(b.Host.ConnectionCount, Is.Zero);
            Assert.That(b.Node.Transport.PeerCount, Is.Zero);
        }

        await using QuicNode a = await QuicNode.Start(TestItem.PrivateKeyA);
        RegisterRecovery(a, b);
        RegisterRecovery(b, a);
        Assert.That(await a.Host.DialAsync(b.Record, CancellationToken.None), Is.Not.Null);
        await Until(() => b.Node.Transport.PeerCount == 1, "the listener still accepting");
    }

    [Test]
    public async Task Static_peer_keeps_a_reserved_slot()
    {
        NodeRecord unreachable = new();
        unreachable.SetEntry(new IpEntry(IPAddress.Loopback));
        unreachable.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyC.CompressedPublicKey));
        unreachable.SetEntry(new LeanqEntry(9));
        unreachable.EnrSequence = 1;
        new NodeRecordSigner(new Ecdsa(), TestItem.PrivateKeyC).Sign(unreachable);
        await using QuicNode b = await QuicNode.Start(TestItem.PrivateKeyB, configure: config =>
        {
            config.MaxActivePeers = 2;
            config.LeanEthp2pStaticPeers = unreachable.ToString();
        });
        await using QuicNode a = await QuicNode.Start(TestItem.PrivateKeyA);
        await using QuicNode c = await QuicNode.Start(TestItem.PrivateKeyC);
        await using QuicNode d = await QuicNode.Start(TestItem.PrivateKeyD);
        foreach (QuicNode peer in new[] { a, c, d })
        {
            RegisterRecovery(peer, b);
            RegisterRecovery(b, peer);
        }

        Assert.That(await a.Host.DialAsync(b.Record, CancellationToken.None), Is.Not.Null);
        await Until(() => b.Host.ConnectionCount == 1, "a in the free slot");
        await d.Host.DialAsync(b.Record, CancellationToken.None);
        await Until(() => d.Host.ConnectionCount == 0, "b refusing d, as the other slot is the static peer's");
        Assert.That(b.Host.ConnectionCount, Is.EqualTo(1));

        Assert.That(await c.Host.DialAsync(b.Record, CancellationToken.None), Is.Not.Null);
        await Until(() => b.Host.ConnectionCount == 2, "the static peer in its reserved slot");
    }

    [Test]
    public async Task Stream_count_limit_enforces_the_receivers_stream_budget()
    {
        await using QuicNode b = await QuicNode.Start(TestItem.PrivateKeyB);
        using X509Certificate2 certificate = LeanEthp2pIdentity.CreateCertificate(TestItem.PrivateKeyA);
        await using QuicConnection client = await QuicConnection.ConnectAsync(RawClient(b.Host.LocalEndPoint!, LeanEthp2pProtocol.Alpn, certificate));
        List<QuicStream> streams = [];
        try
        {
            for (int i = 0; i < LeanEthp2pProtocol.MaxInboundStreams; i++)
                streams.Add(await client.OpenOutboundStreamAsync(QuicStreamType.Unidirectional).AsTask().WaitAsync(Timeout));
            using CancellationTokenSource wait = new(TimeSpan.FromMilliseconds(500));
            Assert.That(async () => await client.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, wait.Token),
                Throws.InstanceOf<OperationCanceledException>(), "the next stream waits for an allowance");
            Assert.That(async () => await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, CancellationToken.None).AsTask()
                .WaitAsync(TimeSpan.FromMilliseconds(500)), Throws.InstanceOf<TimeoutException>(), "no bidirectional streams are granted");
        }
        finally
        {
            foreach (QuicStream stream in streams) await stream.DisposeAsync();
        }
    }

    [Test]
    public async Task Local_record_advertises_leanq_for_the_bound_port_under_the_node_key()
    {
        await using QuicNode a = await QuicNode.Start(TestItem.PrivateKeyA);
        NodeRecord inner = new();
        inner.SetEntry(new IpEntry(IPAddress.Loopback));
        inner.SetEntry(new SecP256k1Entry(TestItem.PrivateKeyA.CompressedPublicKey));
        inner.EnrSequence = 7;
        new NodeRecordSigner(new Ecdsa(), TestItem.PrivateKeyA).Sign(inner);
        INodeRecordProvider provider = Substitute.For<INodeRecordProvider>();
        provider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(inner);

        NodeRecord record = await new LeanEthp2pNodeRecordProvider(provider, a.Host, new Ecdsa(), new InsecureProtectedPrivateKey(TestItem.PrivateKeyA))
            .GetCurrentAsync();

        Assert.That(LeanEthp2pRecord.TryParse(record.ToString(), out LeanEthp2pRecord? parsed, out string? error), Is.True, error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsed!.EndPoint, Is.EqualTo(a.Host.LocalEndPoint));
            Assert.That(parsed.NodeKey, Is.EqualTo(TestItem.PrivateKeyA.PublicKey));
            Assert.That(record.EnrSequence, Is.EqualTo(8UL), "a higher sequence than the record without leanq");
        }
    }

    [Test]
    public async Task Simultaneous_dials_leave_one_connection_per_node()
    {
        await using QuicNode a = await QuicNode.Start(TestItem.PrivateKeyA);
        await using QuicNode b = await QuicNode.Start(TestItem.PrivateKeyB);
        RegisterRecovery(a, b);
        RegisterRecovery(b, a);

        await Task.WhenAll(a.Host.DialAsync(b.Record, CancellationToken.None), b.Host.DialAsync(a.Record, CancellationToken.None));

        await Until(() => a.Host.ConnectionCount == 1 && b.Host.ConnectionCount == 1 && a.Node.Transport.PeerCount == 1
            && b.Node.Transport.PeerCount == 1, "one connection each");
        await Task.Delay(500);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(a.Host.ConnectionCount, Is.EqualTo(1));
            Assert.That(b.Host.ConnectionCount, Is.EqualTo(1));
        }
    }
}
