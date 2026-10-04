// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Logging;
using Nethermind.Network.Libp2p;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P.ReqResp;

/// <summary>
/// phase0 p2p ssz_snappy: bytes remaining after the n SSZ bytes of a request are invalid input. A request is complete once its payload
/// is read, so it is served at once without waiting for EOF; bytes already buffered are answered InvalidRequest, later ones are reported against the peer.
/// </summary>
public class TrailingRequestBytesLoopbackTests
{
    private const string StatusV2 = "/eth2/beacon_chain/req/status/2/ssz_snappy";
    private const string BlocksByRange = "/eth2/beacon_chain/req/beacon_blocks_by_range/2/ssz_snappy";
    private const string BlocksByRoot = "/eth2/beacon_chain/req/beacon_blocks_by_root/2/ssz_snappy";
    private const string MetaData = "/eth2/beacon_chain/req/metadata/3/ssz_snappy";
    private const string Goodbye = "/eth2/beacon_chain/req/goodbye/1/ssz_snappy";

    private const string EnvelopesByRoot = "/eth2/beacon_chain/req/execution_payload_envelopes_by_root/1/ssz_snappy";
    private const string ColumnsByRoot = "/eth2/beacon_chain/req/data_column_sidecars_by_root/1/ssz_snappy";

    // Under the listener's 10 s response timeout, so an answer that only comes from that timeout fails here.
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(5);

    // A listener that pauses or waits for the requester's EOF before serving takes longer than this; the fastest of several requests keeps a slow runner from failing it.
    private static readonly TimeSpan AtOnce = TimeSpan.FromMilliseconds(250);

    [Test]
    [CancelAfter(60_000)]
    public async Task Trailing_bytes_are_refused_and_a_clean_request_is_served_whether_or_not_the_stream_is_held_open(
        [Values(StatusV2, BlocksByRange, BlocksByRoot, Goodbye)] string protocolId,
        [Values(0, 1, 300)] int trailingBytes,
        [Values] bool holdOpen,
        CancellationToken token) =>
        await AssertTrailingBytesAsync(protocolId, await EncodeAsync(protocolId, trailingBytes, token), trailingBytes, holdOpen, token);

    [Test]
    [CancelAfter(60_000)]
    public async Task Trailing_bytes_after_an_empty_by_root_list_are_refused_and_the_list_is_served_on_a_held_open_stream(
        [Values(0, 1, 300)] int trailingBytes,
        [Values] bool holdOpen,
        CancellationToken token) =>
        await AssertTrailingBytesAsync(BlocksByRoot, [.. EmptyListFraming, .. TrailingBytes(trailingBytes)], trailingBytes, holdOpen, token);

    [Test]
    [CancelAfter(60_000)]
    public async Task Skippable_frames_after_the_data_frame_of_an_empty_by_root_list_are_refused(
        [Values(new byte[] { 0xfe }, new byte[] { 0xfe, 0x00, 0x00, 0x00 })] byte[] trailing,
        [Values] bool holdOpen,
        CancellationToken token) =>
        await AssertTrailingBytesAsync(BlocksByRoot, [.. EmptyListFraming, .. trailing], trailing.Length, holdOpen, token);

    [Test]
    [CancelAfter(60_000)]
    public async Task A_request_with_nothing_after_it_on_a_held_open_stream_is_answered_at_once_and_not_reported(
        [Values(StatusV2, BlocksByRange, BlocksByRoot, MetaData, Goodbye)] string protocolId,
        CancellationToken token)
    {
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        Identity requester = new(privateKey: null, KeyType.Secp256K1);
        peer.Id.Returns($"/ip4/127.0.0.1/tcp/4001/p2p/{requester.PeerId}");
        IBeaconSyncPeerPool pool = Substitute.For<IBeaconSyncPeerPool>();
        pool.GetBestPeers(Arg.Any<ulong>()).Returns([peer]);
        await using BeaconP2P server = PeerSessionNodes.Create(peerPool: new Lazy<IBeaconSyncPeerPool>(() => pool)).P2P;
        await server.StartAsync(token);
        byte[] wire = await EncodeAsync(protocolId, 0, token);

        (byte[] response, TimeSpan elapsed) = await RequestAsync(server, protocolId, wire, halfClose: false, token, requester, requests: 3);

        using MemoryStream responseStream = new(response);
        ResponseChunk? chunk = await ReqRespFraming.ReadResponseChunkAsync(responseStream, contextBytesLength: 0, ReqRespFraming.MaxPayloadSize, token);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        if (protocolId == MetaData)
        {
            Assert.That(chunk?.Result, Is.EqualTo(ReqRespFraming.ResponseCode.Success), "metadata is answered, not dropped");
        }
        else
        {
            AssertCleanRequestAnswer(protocolId, chunk);
        }

        Assert.That(elapsed, Is.LessThan(AtOnce), "served without waiting for the requester to end its stream");
        peer.DidNotReceiveWithAnyArgs().ReportFailure(default, default);
    }

    // The empty list is one zero length prefix, alone or with the stream identifier a client may write after it.
    [Test]
    [CancelAfter(60_000)]
    public async Task An_empty_by_root_request_on_a_held_open_stream_is_answered_at_once(
        [Values(BlocksByRoot, EnvelopesByRoot, ColumnsByRoot)] string protocolId,
        [Values(new byte[] { 0x00 }, new byte[] { 0x00, 0xff, 0x06, 0x00, 0x00, 0x73, 0x4e, 0x61, 0x50, 0x70, 0x59 })] byte[] wire,
        CancellationToken token)
    {
        await using BeaconP2P server = PeerSessionNodes.Create().P2P;
        await server.StartAsync(token);

        (byte[] response, TimeSpan elapsed) = await RequestAsync(server, protocolId, wire, halfClose: false, token, requests: 3);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(response, Is.Empty, "an empty list has nothing to answer with");
        Assert.That(elapsed, Is.LessThan(AtOnce), "the request is complete without a data frame or an EOF");
    }

    // Metadata has no payload, so bytes sent with it can arrive after the answer is on its way: they are refused only when already buffered.
    [Test]
    [CancelAfter(60_000)]
    public async Task A_metadata_request_is_answered_and_bytes_sent_with_it_never_stall_it(
        [Values(1, 300)] int trailingBytes,
        [Values] bool holdOpen,
        CancellationToken token)
    {
        await using BeaconP2P server = PeerSessionNodes.Create().P2P;
        await server.StartAsync(token);
        (byte[] response, TimeSpan elapsed) = await RequestAsync(server, MetaData, TrailingBytes(trailingBytes), halfClose: !holdOpen, token);

        using MemoryStream responseStream = new(response);
        ResponseChunk? chunk = await ReqRespFraming.ReadResponseChunkAsync(responseStream, contextBytesLength: 0, ReqRespFraming.MaxPayloadSize, token);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(chunk?.Result, Is.AnyOf(ReqRespFraming.ResponseCode.Success, ReqRespFraming.ResponseCode.InvalidRequest));
        Assert.That(elapsed, Is.LessThan(Prompt));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Bytes_sent_while_a_request_is_served_are_reported_against_the_peer_and_the_response_is_intact(CancellationToken token)
    {
        using ServingGate gate = new();
        TaskCompletionSource<PeerFailureReason> reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Identity requester = new(privateKey: null, KeyType.Secp256K1);
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.Id.Returns($"/ip4/127.0.0.1/tcp/4001/p2p/{requester.PeerId}");
        peer.When(static p => p.ReportFailure(Arg.Any<PeerFailureReason>(), Arg.Any<string?>()))
            .Do(call => reported.TrySetResult(call.ArgAt<PeerFailureReason>(0)));
        IBeaconSyncPeerPool pool = Substitute.For<IBeaconSyncPeerPool>();
        pool.GetBestPeers(Arg.Any<ulong>()).Returns([peer]);
        await using BeaconP2P server = PeerSessionNodes.Create(gate, peerPool: new Lazy<IBeaconSyncPeerPool>(() => pool)).P2P;
        await server.StartAsync(token);

        byte[] wire = await EncodeAsync(StatusV2, 0, token);
        gate.Arm();
        (byte[] response, _) = await RequestAsync(server, StatusV2, wire, halfClose: false, token, requester, afterRequest: async (channel, requestToken) =>
        {
            await gate.Entered.WaitAsync(requestToken);
            await channel.WriteAsync(new ReadOnlySequence<byte>(TrailingBytes(3)), requestToken);
            await reported.Task.WaitAsync(requestToken);
            gate.Release();
        });

        using MemoryStream responseStream = new(response);
        ResponseChunk? chunk = await ReqRespFraming.ReadResponseChunkAsync(responseStream, contextBytesLength: 0, ReqRespFraming.MaxPayloadSize, token);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(reported.Task.Result, Is.EqualTo(PeerFailureReason.ProtocolViolation));
        Assert.That(chunk?.Result, Is.EqualTo(ReqRespFraming.ResponseCode.Success), "the response to the request itself is still sent");
        Assert.That(chunk?.Payload, Is.EqualTo(StatusMessageV2.Encode(PeerSessionNodes.Status)));
        Assert.That(responseStream.Position, Is.EqualTo(response.Length), "a single response_chunk");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Bytes_sent_after_the_response_is_complete_are_reported_against_the_peer(
        [Values(StatusV2, MetaData, Goodbye)] string protocolId,
        CancellationToken token)
    {
        TaskCompletionSource<PeerFailureReason> reported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Identity requester = new(privateKey: null, KeyType.Secp256K1);
        IBeaconSyncPeer peer = Substitute.For<IBeaconSyncPeer>();
        peer.Id.Returns($"/ip4/127.0.0.1/tcp/4001/p2p/{requester.PeerId}");
        peer.When(static p => p.ReportFailure(Arg.Any<PeerFailureReason>(), Arg.Any<string?>()))
            .Do(call => reported.TrySetResult(call.ArgAt<PeerFailureReason>(0)));
        IBeaconSyncPeerPool pool = Substitute.For<IBeaconSyncPeerPool>();
        pool.GetBestPeers(Arg.Any<ulong>()).Returns([peer]);
        await using BeaconP2P server = PeerSessionNodes.Create(peerPool: new Lazy<IBeaconSyncPeerPool>(() => pool)).P2P;
        await server.StartAsync(token);

        byte[] wire = await EncodeAsync(protocolId, 0, token);
        await RequestAsync(server, protocolId, wire, halfClose: false, token, requester, afterResponse: async (channel, requestToken) =>
        {
            await channel.WriteAsync(new ReadOnlySequence<byte>(TrailingBytes(3)), requestToken);
            await reported.Task.WaitAsync(TimeSpan.FromSeconds(3), requestToken);
        });

        Assert.That(reported.Task.Result, Is.EqualTo(PeerFailureReason.ProtocolViolation));
    }

    // Zero length prefix, then a stream identifier and one compressed data frame holding an empty snappy block.
    private static readonly byte[] EmptyListFraming = [0x00, 0xff, 0x06, 0x00, 0x00, 0x73, 0x4e, 0x61, 0x50, 0x70, 0x59, 0x00, 0x05, 0x00, 0x00, 0xd8, 0xea, 0x82, 0xa2, 0x00];

    private static async Task AssertTrailingBytesAsync(string protocolId, byte[] wire, int trailingBytes, bool holdOpen, CancellationToken token)
    {
        await using BeaconP2P server = PeerSessionNodes.Create().P2P;
        await server.StartAsync(token);
        (byte[] response, TimeSpan elapsed) = await RequestAsync(server, protocolId, wire, halfClose: !holdOpen, token);

        using MemoryStream responseStream = new(response);
        ResponseChunk? chunk = await ReqRespFraming.ReadResponseChunkAsync(responseStream, contextBytesLength: 0, ReqRespFraming.MaxPayloadSize, token);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        if (trailingBytes > 0)
        {
            Assert.That(chunk?.Result, Is.EqualTo(ReqRespFraming.ResponseCode.InvalidRequest), "bytes after the request payload are invalid input");
        }
        else
        {
            AssertCleanRequestAnswer(protocolId, chunk);
            Assert.That(responseStream.Position, Is.EqualTo(response.Length), "the response MUST consist of a single response_chunk");
        }

        Assert.That(elapsed, Is.LessThan(Prompt), "answered before the listener's own timeout, without waiting for EOF");
    }

    // The exact answer to a request with nothing after it: a missing chunk must not pass for a served one.
    private static void AssertCleanRequestAnswer(string protocolId, ResponseChunk? chunk)
    {
        if (protocolId == BlocksByRoot)
        {
            Assert.That(chunk, Is.Null, "an unknown root has no block to send");
            return;
        }

        Assert.That(chunk, Is.Not.Null, "a request with nothing after it is answered");
        byte expected = protocolId == BlocksByRange ? ReqRespFraming.ResponseCode.ResourceUnavailable : ReqRespFraming.ResponseCode.Success;
        Assert.That(chunk!.Value.Result, Is.EqualTo(expected));
        if (protocolId == Goodbye)
        {
            Assert.That(chunk.Value.Payload, Has.Length.EqualTo(sizeof(ulong)), "the response is a single uint64");
        }
    }

    private static async Task<byte[]> EncodeAsync(string protocolId, int trailingBytes, CancellationToken token)
    {
        using MemoryStream stream = new();
        switch (protocolId)
        {
            case StatusV2:
                await ReqRespFraming.WriteRequestAsync(stream, StatusMessageV2.Encode(new StatusMessageV2 { ForkDigest = new byte[4], FinalizedRoot = Hash256.Zero, HeadRoot = Hash256.Zero }), token);
                break;
            case BlocksByRange:
                await ReqRespFraming.WriteRequestAsync(stream, BeaconBlocksByRangeRequest.Encode(new BeaconBlocksByRangeRequest { StartSlot = 1, Count = 1, Step = 1 }), token);
                break;
            case BlocksByRoot:
                await ReqRespFraming.WriteRequestAsync(stream, new byte[Hash256.Size], token);
                break;
            case Goodbye:
                await ReqRespFraming.WriteRequestAsync(stream, new byte[sizeof(ulong)], token);
                break;
        }

        stream.Write(TrailingBytes(trailingBytes));
        return stream.ToArray();
    }

    private static byte[] TrailingBytes(int count) => [.. Enumerable.Repeat((byte)0x2a, count)];

    private static async Task<(byte[] Response, TimeSpan Elapsed)> RequestAsync(BeaconP2P server, string protocolId, byte[] wire, bool halfClose, CancellationToken token,
        Identity? requesterIdentity = null, int requests = 1, Func<IChannel, CancellationToken, Task>? afterRequest = null, Func<IChannel, CancellationToken, Task>? afterResponse = null)
    {
        ServiceProvider services = new ServiceCollection()
            .AddSingleton(BeaconP2P.CreateLibp2pLoggerFactory(LimboLogs.Instance))
            .AddSingleton<RawRequestProtocol>()
            .AddLibp2p(static builder => builder.AddProtocol<RawRequestProtocol>())
            .BuildServiceProvider();
        await using (services)
        await using (ILocalPeer requester = services.GetRequiredService<IPeerFactory>().Create(requesterIdentity ?? new Identity(privateKey: null, KeyType.Secp256K1)))
        {
            ISession session = await PeerSessionNodes.DialFromPlainPeerAsync(requester, server, token);
            RawRequestProtocol protocol = services.GetRequiredService<RawRequestProtocol>();
            protocol.Id = protocolId;
            protocol.HalfClose = halfClose;
            protocol.AfterRequest = afterRequest;
            protocol.AfterResponse = afterResponse;

            byte[] response = [];
            TimeSpan fastest = TimeSpan.MaxValue;
            for (int i = 0; i < requests; i++)
            {
                Stopwatch elapsed = Stopwatch.StartNew();
                response = await session.DialAsync<RawRequestProtocol, byte[], byte[]>(wire, token).WaitAsync(token);
                fastest = elapsed.Elapsed < fastest ? elapsed.Elapsed : fastest;
            }

            return (response, fastest);
        }
    }

    /// <summary>A status source that holds the listener mid-request until released, so bytes can be sent while it is serving.</summary>
    private sealed class ServingGate : IBeaconChainStatusSource, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private volatile bool _armed;
        private volatile TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Arm()
        {
            _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release.Reset();
            _armed = true;
        }

        public void Release() => _release.Set();

        public StatusMessageV2 CurrentStatus
        {
            get
            {
                if (_armed)
                {
                    _entered.TrySetResult();
                    _release.Wait(TimeSpan.FromSeconds(30));
                }

                return PeerSessionNodes.Status;
            }
        }

        public Hash256 JustifiedRoot => Hash256.Zero;

        public bool ExecutionInSync => false;

        public void Dispose() => _release.Dispose();
    }

    /// <summary>Sends the given bytes as a whole request, optionally half-closes, then reads the response to its end.</summary>
    private sealed class RawRequestProtocol : ISessionProtocol<byte[], byte[]>
    {
        public string Id { get; set; } = "/test/raw-request/1";

        public bool HalfClose { get; set; } = true;

        public Func<IChannel, CancellationToken, Task>? AfterRequest { get; set; }

        /// <summary>Runs once the response has been read to its end.</summary>
        public Func<IChannel, CancellationToken, Task>? AfterResponse { get; set; }

        public async Task<byte[]> DialAsync(IChannel downChannel, ISessionContext context, byte[] request)
        {
            using CancellationTokenSource cts = new(Prompt + Prompt);
            ChannelStreamAdapter stream = new(downChannel);
            if (request.Length > 0)
            {
                await stream.WriteAsync(request, cts.Token);
            }

            if (HalfClose)
            {
                await downChannel.WriteEofAsync(cts.Token);
            }

            if (AfterRequest is not null)
            {
                await AfterRequest(downChannel, cts.Token);
            }

            using MemoryStream response = new();
            await stream.CopyToAsync(response, cts.Token);
            if (AfterResponse is not null)
            {
                await AfterResponse(downChannel, cts.Token);
            }

            return response.ToArray();
        }

        public Task ListenAsync(IChannel downChannel, ISessionContext context) => throw new NotSupportedException();
    }
}
