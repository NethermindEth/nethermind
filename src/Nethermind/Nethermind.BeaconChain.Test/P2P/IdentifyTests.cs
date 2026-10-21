// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Concurrent;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.BeaconChain.P2P;
using Nethermind.Core;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using Nethermind.Network.Libp2p;
using NSubstitute;
using IdentifyMessage = Nethermind.Libp2p.Protocols.Identify.Dto.Identify;

namespace Nethermind.BeaconChain.Test.P2P;

// One test changes the process-wide ProductInfo.PublicClientId.
[NonParallelizable]
public class IdentifyTests
{
    private const string IdentifyProtocolId = "/ipfs/id/1.0.0";
    private const string IdentifyPushProtocolId = "/ipfs/id/push/1.0.0";
    private const string ServerAgent = "test-server/identify-1.0.0";
    private const int MaxIdentifyMessageSize = 8 * 1024;

    [Test]
    public void Recording_admitted_peer_metadata_evicts_inactive_entries_at_capacity()
    {
        const int capacity = 3;
        PeerStore store = new(capacity);
        PeerId[] peers = [.. Enumerable.Range(0, capacity + 1).Select(_ => new Identity().PeerId)];
        foreach (PeerId peer in peers)
            BeaconP2P.RecordIdentify(store, peer, new IdentifyResult { PeerId = peer, AgentVersion = ServerAgent, Protocols = [IdentifyProtocolId] });

        Assert.That(store.TryGetPeerInfo(peers[0], out _), Is.False, "the oldest inactive peer must not survive capacity pressure");
        foreach (PeerId peer in peers.Skip(1))
        {
            Assert.That(store.TryGetPeerInfo(peer, out PeerStore.PeerInfo? info), Is.True);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(info!.AgentVersion, Is.EqualTo(ServerAgent));
                Assert.That(info.SupportedProtocols, Is.EqualTo(new[] { IdentifyProtocolId }));
            }
        }
    }

    public enum Answer
    {
        Valid,
        KeyOfAnotherPeer,
        RecordSignedByAnotherPeer,
        NoRecord,
        NoAgentVersion,
        MalformedRecord,
    }

    public enum IdentifyReader
    {
        BeaconDial,
        SharedDial,
        SharedPushListener,
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Identify_advertises_its_own_protocol_id_once_and_the_client_agent_string(CancellationToken token)
    {
        await using BeaconP2P server = PeerSessionNodes.Create().P2P;
        await using BeaconP2P client = PeerSessionNodes.Create().P2P;
        await server.StartAsync(token);
        await client.StartAsync(token);

        ISession session = await PeerSessionNodes.DialAsync(client, server, token);
        BeaconP2P.SessionInfo info = await client.GetSessionInfoAsync(session, token);
        string[] advertised = info.Identify?.Protocols.ToArray() ?? [];

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(advertised.Count(static id => id == IdentifyProtocolId), Is.EqualTo(1), "identify is advertised once");
        Assert.That(advertised.Count(static id => id == IdentifyPushProtocolId), Is.EqualTo(1), "identify push is advertised once");
        Assert.That(advertised, Does.Contain("/eth2/beacon_chain/req/status/2/ssz_snappy"), "the rest of the stack is still advertised");
        Assert.That(info.AgentVersion, Is.EqualTo(BeaconP2P.ClientAgentVersion), "the agent string a peer reads is the one the API reports");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Identify_push_advertises_the_identify_protocol_id_once(CancellationToken token)
    {
        await using BeaconP2P server = PeerSessionNodes.Create().P2P;
        await using BeaconP2P client = PeerSessionNodes.Create().P2P;
        await server.StartAsync(token);
        await client.StartAsync(token);

        ISession session = await PeerSessionNodes.DialAsync(client, server, token);
        await client.GetSessionInfoAsync(session, token);
        PeerStore.PeerInfo serverInfo = client.PeerInfoForTest(server.LocalPeerId!);
        // Forgotten, so the wait below ends only once the push has landed.
        serverInfo.SupportedProtocols = null;
        serverInfo.Seq = null;

        Multiaddress extra = Multiaddress.Decode($"/ip4/127.0.0.1/tcp/1/p2p/{server.LocalPeerId}");
        server.LocalPeerForTest!.ListenAddresses.Add(extra);
        await PeerSessionNodes.WaitUntilAsync(() => serverInfo.SupportedProtocols is not null, "the client never applied the server's identify push", token);

        Assert.That((serverInfo.SupportedProtocols ?? []).Count(static id => id == IdentifyProtocolId), Is.EqualTo(1));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_session_reads_the_agent_version_from_one_identify_exchange([Values] bool nodeDials, CancellationToken token)
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
        }), token, pingOnDial: true, identity: new Identity(privateKey: null, KeyType.Secp256K1));
        Multiaddress address = server.Address;

        // The library's own peer identifies exactly once, which fixes what one answer costs the counter.
        await using ServiceProvider plainServices = new ServiceCollection().AddLibp2p(static builder => builder).BuildServiceProvider();
        await using (ILocalPeer plain = plainServices.GetRequiredService<IPeerFactory>().Create(new Identity(privateKey: null, KeyType.Secp256K1)))
        {
            await plain.DialAsync(address, token);
        }

        int oneAnswer = answers!.Reads;
        await using BeaconP2P client = PeerSessionNodes.Create().P2P;
        await client.StartAsync(token);
        Volatile.Write(ref holdAnswer, 1);
        Task<ISession> dial = nodeDials
            ? client.DialPeerAsync(address, token)
            : PeerSessionNodes.DialFromPlainPeerAsync(server.Peer, client, token);
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(oneAnswer, Is.Positive, "fixture: an identify answer reads the advertised protocols");
        Assert.That(info.AgentVersion, Is.EqualTo(ServerAgent));
        Assert.That(answers.Reads - oneAnswer, Is.EqualTo(oneAnswer), "the session asked for identify once");
        Assert.That(info.Identify?.Protocols, Does.Contain(IdentifyProtocolId), "the answer is kept in the session information");
        Assert.That(client.HasRecordedPeerInfoForTest(server.Peer.Identity.PeerId), Is.False, "identify metadata is recorded only after admission");
    }

    [TestCase(Answer.Valid, PeerRecordsVerificationPolicy.RequireCorrect, true)]
    [TestCase(Answer.Valid, PeerRecordsVerificationPolicy.RequireWithWarning, true)]
    [TestCase(Answer.KeyOfAnotherPeer, PeerRecordsVerificationPolicy.DoesNotRequire, false)]
    [TestCase(Answer.RecordSignedByAnotherPeer, PeerRecordsVerificationPolicy.RequireCorrect, false)]
    [TestCase(Answer.RecordSignedByAnotherPeer, PeerRecordsVerificationPolicy.RequireWithWarning, true)]
    [TestCase(Answer.NoRecord, PeerRecordsVerificationPolicy.RequireCorrect, false)]
    [TestCase(Answer.NoRecord, PeerRecordsVerificationPolicy.RequireWithWarning, true)]
    [TestCase(Answer.NoAgentVersion, PeerRecordsVerificationPolicy.RequireCorrect, true)]
    [TestCase(Answer.MalformedRecord, PeerRecordsVerificationPolicy.RequireCorrect, false)]
    [TestCase(Answer.MalformedRecord, PeerRecordsVerificationPolicy.RequireWithWarning, true)]
    public async Task An_identify_answer_is_accepted_only_for_the_authenticated_peer(Answer answer, PeerRecordsVerificationPolicy policy, bool accepted)
    {
        Identity remote = new(privateKey: null, KeyType.Secp256K1);
        Identity other = new(privateKey: null, KeyType.Secp256K1);
        IdentifyMessage message = AnswerOf(answer == Answer.KeyOfAnotherPeer ? other : remote,
            answer switch { Answer.NoRecord => null, Answer.RecordSignedByAnotherPeer => other, _ => remote }, 7, IdentifyProtocolId);
        if (answer == Answer.NoAgentVersion)
        {
            message.ClearAgentVersion();
        }

        if (answer == Answer.MalformedRecord)
        {
            message.SignedPeerRecord = ByteString.CopyFrom([0x0a, 0xff]);
        }

        Libp2p.Core.State session = SessionWith(remote);
        PeerStore peerStore = new();
        IdentifyProtocolSettings settings = new() { PeerRecordsVerificationPolicy = policy };

        if (!accepted)
        {
            await Assert.ThrowsAsync<PeerConnectionException>(async () => await VerifyAndRecord(message, session, settings, peerStore));
            return;
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(await VerifyAndRecord(message, session, settings, peerStore),
            answer == Answer.NoAgentVersion ? Is.Null : Is.EqualTo(ServerAgent), "the agent string is unknown only when the answer carries none");
        Assert.That(peerStore.GetPeerInfo(remote.PeerId).SupportedProtocols, Is.EqualTo(new[] { IdentifyProtocolId }));
        bool verified = answer is Answer.Valid or Answer.NoAgentVersion;
        Assert.That(peerStore.GetPeerInfo(remote.PeerId).Seq, Is.EqualTo(verified ? 7UL : null), "only a verified record's sequence is kept");
        Assert.That(peerStore.GetPeerInfo(remote.PeerId).SignedPeerRecord, verified ? Is.EqualTo(message.SignedPeerRecord) : Is.Null,
            "pubsub peer exchange hands out the stored record, so only a verified one is stored");
    }

    [TestCase(4UL, false, false)]
    [TestCase(5UL, false, false)]
    [TestCase(6UL, false, true)]
    [TestCase(6UL, true, false)]
    public async Task A_later_identify_answer_replaces_the_stored_record_only_when_it_verifies_and_its_sequence_is_newer(ulong seq, bool signedByAnother, bool replaces)
    {
        Identity remote = new(privateKey: null, KeyType.Secp256K1);
        Libp2p.Core.State session = SessionWith(remote);
        PeerStore peerStore = new();
        IdentifyProtocolSettings settings = new() { PeerRecordsVerificationPolicy = PeerRecordsVerificationPolicy.RequireWithWarning };
        IdentifyMessage first = AnswerOf(remote, remote, 5, "/stored");
        IdentifyMessage later = AnswerOf(remote, signedByAnother ? new Identity(privateKey: null, KeyType.Secp256K1) : remote, seq, "/later");
        await VerifyAndRecord(first, session, settings, peerStore);

        await VerifyAndRecord(later, session, settings, peerStore);

        PeerStore.PeerInfo stored = peerStore.GetPeerInfo(remote.PeerId);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(stored.SupportedProtocols, Is.EqualTo(new[] { "/later" }));
        Assert.That(stored.Seq, Is.EqualTo(replaces ? seq : 5UL));
        Assert.That(stored.SignedPeerRecord, Is.EqualTo(replaces ? later.SignedPeerRecord : first.SignedPeerRecord));
    }

    [Test]
    public async Task A_bounded_protocol_list_is_kept_however_many_ids_an_answer_lists([Values] bool beaconDial)
    {
        Identity remote = new(privateKey: null, KeyType.Secp256K1);
        IdentifyMessage message = AnswerOf(remote, remote, 1, new string('p', 129));
        message.Protocols.AddRange(Enumerable.Range(0, 200).Select(static i => $"/protocol/{i}"));
        PeerStore peerStore = new();

        if (beaconDial)
        {
            await VerifyAndRecord(message, SessionWith(remote), new IdentifyProtocolSettings(), peerStore);
        }
        else
        {
            await ReadIdentify(message, SessionWith(remote), new IdentifyProtocolSettings(), peerStore);
        }
        string[] stored = peerStore.GetPeerInfo(remote.PeerId).SupportedProtocols ?? [];

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(stored, Has.Length.EqualTo(64));
        Assert.That(stored[0], Is.EqualTo("/protocol/0"), "an id over the length bound is dropped, not truncated into another id");
    }

    [Test]
    public async Task An_identify_answer_is_refused_from_a_declared_length_past_the_bound(
        [Values(MaxIdentifyMessageSize, MaxIdentifyMessageSize + 1, 256 * 1024 * 1024)] int declared,
        [Values] IdentifyReader reader)
    {
        TimeSpan bound = TimeSpan.FromSeconds(10);
        Channel channel = new();
        ISessionContext context = ContextFor(SessionWith(new Identity(privateKey: null, KeyType.Secp256K1)));

        Task read = (reader switch
        {
            IdentifyReader.SharedDial => new IdentifyProtocol(new ProtocolStackSettings(), peerStore: new PeerStore()).DialAsync(channel, context),
            IdentifyReader.SharedPushListener => new IdentifyPushProtocol(new ProtocolStackSettings(), peerStore: new PeerStore()).ListenAsync(channel, context),
            _ => new IdentifyProtocol(new ProtocolStackSettings()).DialAsync(channel, context),
        }).WaitAsync(bound);
        await channel.Reverse.WriteVarintAsync(declared).AsTask().WaitAsync(bound);
        await channel.Reverse.WriteEofAsync().AsTask().WaitAsync(bound);

        if (declared > MaxIdentifyMessageSize)
        {
            await Assert.ThrowsAsync<System.IO.InvalidDataException>(() => read);
        }
        else
        {
            await Assert.ThrowsAsync<ChannelClosedException>(() => read, "a length within the bound goes on to read the body");
        }
    }

    [Test]
    public async Task An_identify_push_updates_only_admitted_peer_metadata([Values] bool admitted)
    {
        TimeSpan bound = TimeSpan.FromSeconds(10);
        Identity remote = new(privateKey: null, KeyType.Secp256K1);
        PeerStore peerStore = new();
        peerStore.GetPeerInfo(remote.PeerId).SupportedProtocols = ["/stored"];
        int announcements = 0;
        peerStore.OnNewPeer += _ => announcements++;

        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(SessionWith(remote));
        Channel channel = new();
        Task listen = new BeaconP2P.AdmittedIdentifyPushProtocol(new ProtocolStackSettings(), new IdentifyProtocolSettings(), peerStore,
            peerId => admitted && peerId == remote.PeerId).ListenAsync(channel, context).WaitAsync(bound);
        if (admitted) await channel.Reverse.WriteSizeAndProtobufAsync(AnswerOf(remote, remote, 1, "/pushed")).AsTask().WaitAsync(bound);
        await listen;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peerStore.GetPeerInfo(remote.PeerId).SupportedProtocols, Is.EqualTo(new[] { admitted ? "/pushed" : "/stored" }));
            Assert.That(announcements, Is.EqualTo(admitted ? 1 : 0));
        }
    }

    [Test]
    public async Task A_partial_identify_push_preserves_omitted_fields([Values] bool protocolsOnly)
    {
        Identity remote = new(privateKey: null, KeyType.Secp256K1);
        PeerStore peerStore = new();
        IdentifyProtocolSettings settings = new() { PeerRecordsVerificationPolicy = PeerRecordsVerificationPolicy.RequireCorrect };
        IdentifyMessage original = AnswerOf(remote, remote, 1, "/stored");
        await ReadIdentify(original, SessionWith(remote), settings, peerStore);
        IdentifyMessage update = new();
        if (protocolsOnly) update.Protocols.Add("/pushed");
        else update.AgentVersion = "updated-agent";
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(SessionWith(remote));
        Channel channel = new();

        Task listen = new IdentifyPushProtocol(new ProtocolStackSettings(), settings, peerStore).ListenAsync(channel, context);
        await channel.Reverse.WriteSizeAndProtobufAsync(update).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await listen.WaitAsync(TimeSpan.FromSeconds(10));

        PeerStore.PeerInfo stored = peerStore.GetPeerInfo(remote.PeerId);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(stored.SupportedProtocols, Is.EqualTo(new[] { protocolsOnly ? "/pushed" : "/stored" }));
        Assert.That(stored.SignedPeerRecord, Is.EqualTo(original.SignedPeerRecord));
        Assert.That(stored.Seq, Is.EqualTo(1UL));
    }

    [Test]
    public async Task An_identify_push_still_rejects_a_supplied_foreign_key_or_invalid_record([Values] bool foreignKey)
    {
        Identity remote = new(privateKey: null, KeyType.Secp256K1);
        IdentifyMessage update = new();
        if (foreignKey) update.PublicKey = new Identity(privateKey: null, KeyType.Secp256K1).PublicKey.ToByteString();
        else update.SignedPeerRecord = ByteString.Empty;
        IdentifyProtocolSettings settings = new() { PeerRecordsVerificationPolicy = PeerRecordsVerificationPolicy.RequireCorrect };

        Channel channel = new();
        Task listen = new IdentifyPushProtocol(new ProtocolStackSettings(), settings, new PeerStore()).ListenAsync(channel, ContextFor(SessionWith(remote)));
        await channel.Reverse.WriteSizeAndProtobufAsync(update);
        await Assert.ThrowsAsync<PeerConnectionException>(async () => await listen.WaitAsync(TimeSpan.FromSeconds(10)));
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
        IdentifyProtocol probe = new(new ProtocolStackSettings());

        Task read = probe.DialAsync(channel, ContextFor(SessionWith(new Identity(privateKey: null, KeyType.Secp256K1)))).WaitAsync(bound);
        await channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(declared)).AsTask().WaitAsync(bound);
        Task<IOResult> sent = channel.Reverse.WriteAsync(new ReadOnlySequence<byte>(body)).AsTask();
        await Assert.ThrowsAsync<PeerConnectionException>(() => read);
        ReadResult rest = await channel.ReadAsync(body.Length).AsTask().WaitAsync(bound);
        Assert.That(rest.Data.Length, Is.EqualTo(body.Length), "the body is still pending, not buffered by the refused read");
        await sent.WaitAsync(bound);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_session_whose_identify_answer_names_another_peers_key_is_closed_and_never_established([Values] bool nodeDials, CancellationToken token)
    {
        TimeSpan within = BeaconP2P.IdentifyTimeout + TimeSpan.FromSeconds(3);
        await using BeaconP2P node = PeerSessionNodes.Create().P2P;
        await node.StartAsync(token);
        int established = 0;
        node.SessionEstablished += (_, _) => Interlocked.Increment(ref established);
        await using PlainPeer peer = await PlainPeer.StartAsync(static settings => new ForeignKeyIdentifyProtocol(settings), token);

        if (nodeDials)
        {
            Task<ISession> dial = node.DialPeerAsync(peer.Address, token);
            Assert.That(await Task.WhenAny(dial, Task.Delay(within, token)), Is.SameAs(dial), "the dial ended within the identify bound");
            Assert.That(dial.Status, Is.EqualTo(TaskStatus.Faulted), "no session with a misidentified peer is handed back");
        }
        else
        {
            ISession session = await peer.Peer.DialAsync(PeerSessionNodes.LoopbackAddress(node), token).WaitAsync(token);
            // The node completes its side of the upgrade only once the dialer sends on the connection.
            _ = session.DialAsync<PingProtocol>(token);
            await PeerSessionNodes.WaitUntilAsync(() => peer.Peer.Sessions.Count == 0, "the peer's session was left open", token, within);
        }

        await PeerSessionNodes.WaitUntilAsync(() => node.SessionCountForTest == 0, "the misidentified session was left open", token, within);
        Assert.That(Volatile.Read(ref established), Is.Zero, "a misidentified session is never reported as established");
    }

    [TestCase(null, null, ExpectedResult = false, TestName = "Dropped session with no slot")]
    [TestCase(TaskStatus.Canceled, null, ExpectedResult = false, TestName = "Session whose identify failed")]
    [TestCase(TaskStatus.RanToCompletion, null, ExpectedResult = true, TestName = "Identified session")]
    // The session a dial lost is still listed while its identify is pending; handing it back returns a closed, unidentified session.
    [TestCase(TaskStatus.WaitingForActivation, TaskStatus.Canceled, ExpectedResult = false, TestName = "Session still identifying whose identify then fails")]
    [TestCase(TaskStatus.WaitingForActivation, TaskStatus.RanToCompletion, ExpectedResult = true, TestName = "Session still identifying whose identify then completes")]
    public async Task<bool> A_failed_dial_returns_only_a_raced_session_whose_identify_completes(TaskStatus? slotStatus, TaskStatus? laterStatus)
    {
        ISession session = Substitute.For<ISession>();
        ConcurrentDictionary<ISession, TaskCompletionSource<BeaconP2P.SessionInfo>> sessionInfo = new();
        TaskCompletionSource<BeaconP2P.SessionInfo> slot = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (slotStatus is { } status)
        {
            Complete(slot, status);
            sessionInfo[session] = slot;
        }

        Task<ISession?> raced = BeaconP2P.WhenIdentifiedAsync(sessionInfo, session, CancellationToken.None);
        if (laterStatus is { } later)
        {
            Assert.That(raced.IsCompleted, Is.False, "a session still identifying is not decided before its identify ends");
            Complete(slot, later);
        }

        ISession? result = await raced.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(result, Is.Null.Or.SameAs(session));
        return result is not null;

        static void Complete(TaskCompletionSource<BeaconP2P.SessionInfo> slot, TaskStatus status)
        {
            if (status == TaskStatus.Canceled)
                slot.SetCanceled();
            else if (status == TaskStatus.RanToCompletion)
                slot.SetResult(new BeaconP2P.SessionInfo(PeerDirection.Inbound, ServerAgent));
        }
    }

    private static async Task<string?> VerifyAndRecord(IdentifyMessage message, Libp2p.Core.State session, IdentifyProtocolSettings settings, PeerStore peerStore)
    {
        IdentifyResult answer = await ReadIdentify(message, session, settings);
        BeaconP2P.RecordIdentify(peerStore, session.RemotePeerId!, answer);
        return answer.AgentVersion;
    }

    private static async Task<IdentifyResult> ReadIdentify(IdentifyMessage message, Libp2p.Core.State state, IdentifyProtocolSettings settings, PeerStore? store = null)
    {
        Channel channel = new();
        Task<IdentifyResult> read = new IdentifyProtocol(new ProtocolStackSettings(), settings, store).DialAsync(channel, ContextFor(state));
        await channel.Reverse.WriteSizeAndProtobufAsync(message);
        return await read.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static ISessionContext ContextFor(Libp2p.Core.State state)
    {
        ISessionContext context = Substitute.For<ISessionContext>();
        context.State.Returns(state);
        return context;
    }

    private static Multiaddress AddressOf(Identity peer) => Multiaddress.Decode($"/ip4/127.0.0.1/tcp/9000/p2p/{peer.PeerId}");
    private static Libp2p.Core.State SessionWith(Identity remote) => new() { RemoteAddress = AddressOf(remote), RemotePublicKey = remote.PublicKey };

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

    private sealed class ForeignKeyIdentifyProtocol(IProtocolStackSettings settings) : IdentifyProtocol(settings), ISessionListenerProtocol
    {
        public new async Task ListenAsync(IChannel downChannel, ISessionContext context) =>
            await downChannel.WriteSizeAndProtobufAsync(AnswerOf(new Identity(privateKey: null, KeyType.Secp256K1), null, 0, IdentifyProtocolId));
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
