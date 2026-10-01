// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.BeaconChain.P2P;
using Nethermind.Core;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using NSubstitute;
using NUnit.Framework;
using IdentifyMessage = Nethermind.Libp2p.Protocols.Identify.Dto.Identify;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>What a node tells its peers about itself over <c>/ipfs/id/1.0.0</c> and <c>/ipfs/id/push/1.0.0</c>, and what it accepts from theirs.</summary>
/// <remarks>Non-parallelizable because one test changes the process-wide <see cref="ProductInfo.PublicClientId"/>.</remarks>
[NonParallelizable]
public class IdentifyTests
{
    private const string IdentifyProtocolId = "/ipfs/id/1.0.0";
    private const string ServerAgent = "test-server/identify-1.0.0";

    public enum Answer
    {
        Valid,
        KeyOfAnotherPeer,
        RecordSignedByAnotherPeer,
        NoRecord,
        NoAgentVersion,
    }

    [Test]
    [CancelAfter(60_000)]
    public Task Identify_advertises_its_own_protocol_id_once_and_the_client_agent_string(CancellationToken token) =>
        PeerSessionNodes.RetryStalledAsync(Identify_advertises_its_own_protocol_id_once_and_the_client_agent_stringAsync, token);

    private static async Task<bool> Identify_advertises_its_own_protocol_id_once_and_the_client_agent_stringAsync(CancellationToken token)
    {
        await using BeaconP2P server = PeerSessionNodes.Create().P2P;
        await using BeaconP2P client = PeerSessionNodes.Create().P2P;
        await server.StartAsync(token);
        await client.StartAsync(token);

        ISession session = await PeerSessionNodes.DialAsync(client, server, token);
        BeaconP2P.SessionInfo info = await client.GetSessionInfoAsync(session, token);
        string[] advertised = client.PeerInfoForTest(server.LocalPeerId!).SupportedProtocols ?? [];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(advertised.Count(static id => id == IdentifyProtocolId), Is.EqualTo(1), "identify is listed once even though the agent probe shares its id");
            Assert.That(advertised, Does.Contain("/eth2/beacon_chain/req/status/2/ssz_snappy"), "the rest of the stack is still advertised");
            Assert.That(info.AgentVersion, Is.EqualTo(BeaconP2P.ClientAgentVersion), "the agent string a peer reads is the one the API reports");
        }

        return true;
    }

    [Test]
    [CancelAfter(60_000)]
    public Task Identify_push_advertises_the_identify_protocol_id_once(CancellationToken token) =>
        PeerSessionNodes.RetryStalledAsync(Identify_push_advertises_the_identify_protocol_id_onceAsync, token);

    private static async Task<bool> Identify_push_advertises_the_identify_protocol_id_onceAsync(CancellationToken token)
    {
        await using BeaconP2P server = PeerSessionNodes.Create().P2P;
        await using BeaconP2P client = PeerSessionNodes.Create().P2P;
        await server.StartAsync(token);
        await client.StartAsync(token);

        ISession session = await PeerSessionNodes.DialAsync(client, server, token);
        await client.GetSessionInfoAsync(session, token);
        PeerStore.PeerInfo serverInfo = client.PeerInfoForTest(server.LocalPeerId!);
        // The pinned identify applies an answer only over an older record, so the record is forgotten to let the push land.
        serverInfo.SupportedProtocols = null;
        serverInfo.Seq = null;

        // A new listen address makes the server push its identify to every open session.
        Multiaddress extra = Multiaddress.Decode($"/ip4/127.0.0.1/tcp/1/p2p/{server.LocalPeerId}");
        server.LocalPeerForTest!.ListenAddresses.Add(extra);
        await PeerSessionNodes.WaitUntilAsync(() => serverInfo.SupportedProtocols is not null, "the client never applied the server's identify push", token);

        Assert.That((serverInfo.SupportedProtocols ?? []).Count(static id => id == IdentifyProtocolId), Is.EqualTo(1));
        return true;
    }

    [Test]
    [CancelAfter(60_000)]
    public Task A_session_reads_the_agent_version_from_one_identify_exchange([Values] bool nodeDials, CancellationToken token) =>
        PeerSessionNodes.RetryStalledAsync(t => A_session_reads_the_agent_version_from_one_identify_exchangeAsync(nodeDials, t), token);

    private static async Task<bool> A_session_reads_the_agent_version_from_one_identify_exchangeAsync(bool nodeDials, CancellationToken token)
    {
        CountingStackSettings? answers = null;
        int holdAnswer = 0;
        TaskCompletionSource answerStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseAnswer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using PlainPeer server = await PlainPeer.StartAsync(settings => new CountingIdentifyProtocol(answers = new CountingStackSettings(settings), async () =>
        {
            if (Volatile.Read(ref holdAnswer) != 0)
            {
                answerStarted.TrySetResult();
                await releaseAnswer.Task.WaitAsync(token);
            }
        }), token, pingOnDial: true, identity: SigningIdentity());
        Multiaddress address = server.Address;

        // The library's own peer identifies exactly once, which fixes what one answer costs the counter.
        await using ServiceProvider plainServices = new ServiceCollection().AddLibp2p(static builder => builder).BuildServiceProvider();
        await using (ILocalPeer plain = plainServices.GetRequiredService<IPeerFactory>().Create(SigningIdentity()))
        {
            await plain.DialAsync(address, token);
        }

        int oneAnswer = answers!.Reads;
        await using BeaconP2P client = PeerSessionNodes.Create().P2P;
        await client.StartAsync(token);
        Volatile.Write(ref holdAnswer, 1);
        Task<ISession> dial = nodeDials
            ? client.DialPeerAsync(address, token)
            : PeerSessionNodes.DialFromPlainPeerAsync(server.Peer, server.Log, client, token);
        try
        {
            await answerStarted.Task.WaitAsync(token);
            Assert.That(client.TryGetEstablishedSession(server.Peer.Identity.PeerId, out ISession? pending), Is.True);
            Assert.That(client.GetSessionInfoAsync(pending!, token).IsCompleted, Is.False, "session information waits for identify in either direction");
            if (nodeDials)
            {
                Assert.That(dial.IsCompleted, Is.False, "a working session is handed back only after identify completes");
            }
        }
        finally
        {
            releaseAnswer.TrySetResult();
        }

        ISession session = await dial;
        if (!nodeDials)
        {
            Assert.That(client.TryGetEstablishedSession(server.Peer.Identity.PeerId, out ISession? inbound), Is.True);
            session = inbound!;
        }

        BeaconP2P.SessionInfo info = await client.GetSessionInfoAsync(session, token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(oneAnswer, Is.Positive, "fixture: an identify answer reads the advertised protocols");
            Assert.That(info.AgentVersion, Is.EqualTo(ServerAgent));
            Assert.That(answers.Reads - oneAnswer, Is.EqualTo(oneAnswer), "the session asked for identify once");
            Assert.That(client.PeerInfoForTest(server.Peer.Identity.PeerId).SupportedProtocols, Does.Contain(IdentifyProtocolId), "the answer is recorded in the peer store");
        }

        return true;
    }

    [TestCase(Answer.Valid, PeerRecordsVerificationPolicy.RequireCorrect, true)]
    [TestCase(Answer.Valid, PeerRecordsVerificationPolicy.RequireWithWarning, true)]
    [TestCase(Answer.KeyOfAnotherPeer, PeerRecordsVerificationPolicy.DoesNotRequire, false)]
    [TestCase(Answer.RecordSignedByAnotherPeer, PeerRecordsVerificationPolicy.RequireCorrect, false)]
    [TestCase(Answer.RecordSignedByAnotherPeer, PeerRecordsVerificationPolicy.RequireWithWarning, true)]
    [TestCase(Answer.NoRecord, PeerRecordsVerificationPolicy.RequireCorrect, false)]
    [TestCase(Answer.NoRecord, PeerRecordsVerificationPolicy.RequireWithWarning, true)]
    [TestCase(Answer.NoAgentVersion, PeerRecordsVerificationPolicy.RequireCorrect, true)]
    public void An_identify_answer_is_accepted_only_for_the_authenticated_peer(Answer answer, PeerRecordsVerificationPolicy policy, bool accepted)
    {
        Identity remote = SigningIdentity();
        Identity other = SigningIdentity();
        IdentifyMessage message = AnswerOf(answer == Answer.KeyOfAnotherPeer ? other : remote,
            answer switch { Answer.NoRecord => null, Answer.RecordSignedByAnotherPeer => other, _ => remote }, 7, IdentifyProtocolId);
        if (answer == Answer.NoAgentVersion)
        {
            message.ClearAgentVersion();
        }

        Libp2p.Core.State session = SessionWith(remote);
        PeerStore peerStore = new();
        IdentifyProtocolSettings settings = new() { PeerRecordsVerificationPolicy = policy };

        if (!accepted)
        {
            Assert.Throws<PeerConnectionException>(() => IdentifyAgentVersionProbe.Apply(message, session, settings, peerStore));
            return;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(IdentifyAgentVersionProbe.Apply(message, session, settings, peerStore),
                answer == Answer.NoAgentVersion ? Is.Null : Is.EqualTo(ServerAgent), "the agent string is unknown only when the answer carries none");
            Assert.That(peerStore.GetPeerInfo(remote.PeerId).SupportedProtocols, Is.EqualTo(new[] { IdentifyProtocolId }));
            Assert.That(peerStore.GetPeerInfo(remote.PeerId).Seq, Is.EqualTo(answer is Answer.Valid or Answer.NoAgentVersion ? 7UL : 0UL), "only a verified record's sequence is kept");
            Assert.That(peerStore.GetPeerInfo(remote.PeerId).SignedPeerRecord, Is.EqualTo(message.SignedPeerRecord), "pubsub peer exchange hands out the stored record");
        }
    }

    /// <summary>A replayed or older signed record must not roll back what the peer store holds for the peer.</summary>
    [TestCase(4UL, false)]
    [TestCase(5UL, false)]
    [TestCase(6UL, true)]
    public void A_later_identify_answer_replaces_the_stored_record_only_when_its_sequence_is_newer(ulong seq, bool replaces)
    {
        Identity remote = SigningIdentity();
        Libp2p.Core.State session = SessionWith(remote);
        PeerStore peerStore = new();
        IdentifyProtocolSettings settings = new() { PeerRecordsVerificationPolicy = PeerRecordsVerificationPolicy.RequireCorrect };
        IdentifyMessage first = AnswerOf(remote, remote, 5, "/stored");
        IdentifyMessage later = AnswerOf(remote, remote, seq, "/later");
        IdentifyAgentVersionProbe.Apply(first, session, settings, peerStore);

        IdentifyAgentVersionProbe.Apply(later, session, settings, peerStore);

        PeerStore.PeerInfo stored = peerStore.GetPeerInfo(remote.PeerId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.SupportedProtocols, Is.EqualTo(new[] { replaces ? "/later" : "/stored" }));
            Assert.That(stored.Seq, Is.EqualTo(replaces ? seq : 5UL));
            Assert.That(stored.SignedPeerRecord, Is.EqualTo(replaces ? later.SignedPeerRecord : first.SignedPeerRecord));
        }
    }

    /// <summary>Any peer answers identify, so the length it declares must not make the session wait for or buffer an oversized body.</summary>
    [TestCase(IdentifyAgentVersionProbe.MaxMessageSize, false)]
    [TestCase(IdentifyAgentVersionProbe.MaxMessageSize + 1, true)]
    [TestCase(256 * 1024 * 1024, true)]
    public async Task An_identify_answer_is_refused_from_a_declared_length_past_the_bound(int declared, bool refused)
    {
        TimeSpan bound = TimeSpan.FromSeconds(10);
        Channel channel = new();
        IdentifyAgentVersionProbe probe = new(null!, new IdentifyProtocolSettings(), new PeerStore());

        Task read = probe.DialAsync(channel, null!, 0).WaitAsync(bound);
        await channel.Reverse.WriteVarintAsync(declared).AsTask().WaitAsync(bound);
        await channel.Reverse.WriteEofAsync().AsTask().WaitAsync(bound);

        if (refused)
        {
            Assert.ThrowsAsync<PeerConnectionException>(() => read);
        }
        else
        {
            Assert.ThrowsAsync<ChannelClosedException>(() => read, "a length within the bound goes on to read the body");
        }
    }

    /// <summary>A read of declared length 0 takes everything pending, so an answer declaring none must be refused before any of the body is read.</summary>
    [TestCase(new byte[] { 0x00 })]
    // The longest zero encoding the library varint reader accepts: it reads at most five bytes for a length.
    [TestCase(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x00 })]
    public async Task An_identify_answer_declaring_no_bytes_is_refused_without_reading_what_follows(byte[] declared)
    {
        TimeSpan bound = TimeSpan.FromSeconds(10);
        byte[] body = new byte[256 * 1024];
        Channel channel = new();
        IdentifyAgentVersionProbe probe = new(null!, new IdentifyProtocolSettings(), new PeerStore());

        Task read = probe.DialAsync(channel, null!, 0).WaitAsync(bound);
        await channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(declared)).AsTask().WaitAsync(bound);
        Task<IOResult> sent = channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(body)).AsTask();

        Assert.ThrowsAsync<PeerConnectionException>(() => read);
        ReadResult rest = await channel.ReadAsync(body.Length).AsTask().WaitAsync(bound);
        Assert.That(rest.Data.Length, Is.EqualTo(body.Length), "the body is still pending, not buffered by the refused read");
        await sent.WaitAsync(bound);
    }

    /// <summary>An answer that fails verification must cost the peer its session, in either direction, not just the agent string.</summary>
    [Test]
    [CancelAfter(60_000)]
    public Task A_session_whose_identify_answer_names_another_peers_key_is_closed_and_never_established([Values] bool nodeDials, CancellationToken token) =>
        PeerSessionNodes.RetryStalledAsync(t => A_session_whose_identify_answer_names_another_peers_key_is_closed_and_never_establishedAsync(nodeDials, t), token);

    private static async Task<bool> A_session_whose_identify_answer_names_another_peers_key_is_closed_and_never_establishedAsync(bool nodeDials, CancellationToken token)
    {
        TimeSpan within = IdentifyAgentVersionProbe.ReadTimeout + TimeSpan.FromSeconds(3);
        await using BeaconP2P node = PeerSessionNodes.Create().P2P;
        await node.StartAsync(token);
        int established = 0;
        node.SessionEstablished += (_, _) => Interlocked.Increment(ref established);
        await using PlainPeer peer = await PlainPeer.StartAsync(static settings => new ForeignKeyIdentifyProtocol(settings), token);

        if (nodeDials)
        {
            Task<ISession> dial = node.DialPeerAsync(peer.Address, token);
            Assert.That(await Task.WhenAny(dial, Task.Delay(within, token)), Is.SameAs(dial), "the dial ended within the identify bound");
            PeerSessionNodes.ThrowIfIdentifyStalled(node);
            Assert.That(dial.Status, Is.EqualTo(TaskStatus.Faulted), "no session with a misidentified peer is handed back");
        }
        else
        {
            ISession session = await peer.Peer.DialAsync(PeerSessionNodes.LoopbackAddress(node), token).WaitAsync(token);
            // The node completes its side of the upgrade only once the dialer sends on the connection.
            _ = session.DialAsync<PingProtocol>(token);
            await PeerSessionNodes.WaitUntilAsync(() => peer.Peer.Sessions.Count == 0, "the peer's session was left open", token, within, [node]);
            PeerSessionNodes.ThrowIfIdentifyStalled(node);
        }

        await PeerSessionNodes.WaitUntilAsync(() => node.SessionCountForTest == 0, "the misidentified session was left open", token, within, [node]);
        Assert.That(Volatile.Read(ref established), Is.Zero, "a misidentified session is never reported as established");
        return true;
    }

    /// <summary>
    /// A failed dial may hand back only a session the peer opened: one whose identify failed, or that the library already
    /// dropped (its slot removed between the session snapshot and the check), is the dial's own lost session.
    /// </summary>
    [TestCase(null, ExpectedResult = false, TestName = "Dropped session with no slot")]
    [TestCase(TaskStatus.Canceled, ExpectedResult = false, TestName = "Session whose identify failed")]
    [TestCase(TaskStatus.WaitingForActivation, ExpectedResult = true, TestName = "Session still identifying")]
    [TestCase(TaskStatus.RanToCompletion, ExpectedResult = true, TestName = "Identified session")]
    public bool A_failed_dial_returns_only_a_raced_session_that_is_still_tracked(TaskStatus? slotStatus)
    {
        ISession session = Substitute.For<ISession>();
        ConcurrentDictionary<ISession, TaskCompletionSource<BeaconP2P.SessionInfo>> sessionInfo = new();
        if (slotStatus is { } status)
        {
            TaskCompletionSource<BeaconP2P.SessionInfo> slot = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (status == TaskStatus.Canceled)
                slot.SetCanceled();
            else if (status == TaskStatus.RanToCompletion)
                slot.SetResult(new BeaconP2P.SessionInfo(PeerDirection.Inbound, ServerAgent));
            sessionInfo[session] = slot;
        }

        return BeaconP2P.IsNotDropped(sessionInfo, session);
    }

    /// <summary>A fresh secp256k1 identity whose key signs for its own public key.</summary>
    /// <remarks>About one key in 256 from the pinned secp256k1 generation does not sign for its own public key, and so fails
    /// its own peer-record check; every such key seen ends in a zero byte.</remarks>
    private static Identity SigningIdentity()
    {
        byte[] probe = [1];
        while (true)
        {
            Identity identity = new(privateKey: null, KeyType.Secp256K1);
            if (identity.VerifySignature(probe, identity.Sign(probe)))
            {
                return identity;
            }
        }
    }

    private static Multiaddress AddressOf(Identity peer) => Multiaddress.Decode($"/ip4/127.0.0.1/tcp/9000/p2p/{peer.PeerId}");

    private static Libp2p.Core.State SessionWith(Identity remote) => new() { RemoteAddress = AddressOf(remote), RemotePublicKey = remote.PublicKey };

    /// <param name="key">Whose public key the answer carries.</param>
    /// <param name="recordSigner">Who signs the peer record naming the answering peer; <c>null</c> for no record.</param>
    private static IdentifyMessage AnswerOf(Identity key, Identity? recordSigner, ulong seq, string protocol)
    {
        IdentifyMessage message = new() { AgentVersion = ServerAgent, PublicKey = key.PublicKey.ToByteString() };
        message.Protocols.Add(protocol);
        if (recordSigner is not null)
        {
            message.SignedPeerRecord = SigningHelper.CreateSignedEnvelope(recordSigner, [AddressOf(key)], seq);
        }

        return message;
    }

    /// <summary>An operator's public client-id format changes what the node reports elsewhere, not its agent string.</summary>
    [Test]
    public void Agent_version_is_the_client_id_whatever_public_client_id_format_is_configured()
    {
        try
        {
            ProductInfo.InitializePublicClientId("probe/{version}");
            Assert.That(ProductInfo.PublicClientId, Is.Not.EqualTo(ProductInfo.ClientId), "fixture: the public id differs");
            Assert.That(BeaconP2P.ClientAgentVersion, Is.EqualTo(ProductInfo.ClientId));
        }
        finally
        {
            ProductInfo.InitializePublicClientId(ProductInfo.DefaultPublicClientIdFormat);
        }
    }

    /// <summary>Answers identify with the public key of a peer other than the one that holds the session.</summary>
    private sealed class ForeignKeyIdentifyProtocol(IProtocolStackSettings settings) : IdentifyProtocol(settings), ISessionListenerProtocol
    {
        public new async Task ListenAsync(IChannel downChannel, ISessionContext context) =>
            await downChannel.WriteSizeAndProtobufAsync(AnswerOf(SigningIdentity(), null, 0, IdentifyProtocolId));
    }

    private sealed class CountingIdentifyProtocol(IProtocolStackSettings settings, Func<Task> beforeAnswer)
        : IdentifyProtocol(settings, new IdentifyProtocolSettings { AgentVersion = ServerAgent }, new PeerStore()), ISessionListenerProtocol
    {
        public new async Task ListenAsync(IChannel downChannel, ISessionContext context)
        {
            await beforeAnswer();
            await base.ListenAsync(downChannel, context);
        }
    }

    /// <summary>The live stack settings, counting how often identify reads the protocols it advertises.</summary>
    private sealed class CountingStackSettings(IProtocolStackSettings inner) : IProtocolStackSettings
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public Dictionary<ProtocolRef, ProtocolRef[]>? Protocols
        {
            get
            {
                Interlocked.Increment(ref _reads);
                return inner.Protocols;
            }
            set => inner.Protocols = value;
        }

        public ProtocolRef[]? TopProtocols
        {
            get => inner.TopProtocols;
            set => inner.TopProtocols = value;
        }
    }
}
