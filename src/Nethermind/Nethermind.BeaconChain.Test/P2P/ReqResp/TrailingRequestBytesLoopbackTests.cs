// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P.ReqResp;

/// <summary>
/// phase0 p2p ssz_snappy: bytes remaining after the n SSZ bytes of a request are invalid input, answered with InvalidRequest,
/// whether or not the requester half-closes; a clean request on a stream held open must still be served.
/// </summary>
public class TrailingRequestBytesLoopbackTests
{
    private const string StatusV2 = "/eth2/beacon_chain/req/status/2/ssz_snappy";
    private const string BlocksByRange = "/eth2/beacon_chain/req/beacon_blocks_by_range/2/ssz_snappy";
    private const string BlocksByRoot = "/eth2/beacon_chain/req/beacon_blocks_by_root/2/ssz_snappy";
    private const string MetaData = "/eth2/beacon_chain/req/metadata/3/ssz_snappy";
    private const string Goodbye = "/eth2/beacon_chain/req/goodbye/1/ssz_snappy";

    // Under the listener's 10 s response timeout, so an answer that only comes from that timeout fails here.
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(5);

    [Test]
    [CancelAfter(60_000)]
    public async Task Trailing_bytes_are_refused_and_a_clean_request_is_served_whether_or_not_the_stream_is_held_open(
        [Values(StatusV2, BlocksByRange, BlocksByRoot, MetaData, Goodbye)] string protocolId,
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

    // Zero length prefix, then a stream identifier and one compressed data frame holding an empty snappy block.
    private static readonly byte[] EmptyListFraming = [0x00, 0xff, 0x06, 0x00, 0x00, 0x73, 0x4e, 0x61, 0x50, 0x70, 0x59, 0x00, 0x05, 0x00, 0x00, 0xd8, 0xea, 0x82, 0xa2, 0x00];

    private static async Task AssertTrailingBytesAsync(string protocolId, byte[] wire, int trailingBytes, bool holdOpen, CancellationToken token)
    {
        await using BeaconP2P server = PeerSessionNodes.Create().P2P;
        await server.StartAsync(token);
        (byte[] response, TimeSpan elapsed) = await PeerSessionNodes.RetryStalledAsync(attemptToken => RequestAsync(server, protocolId, wire, halfClose: !holdOpen, attemptToken), token);

        using MemoryStream responseStream = new(response);
        ResponseChunk? chunk = await ReqRespFraming.ReadResponseChunkAsync(responseStream, contextBytesLength: 0, ReqRespFraming.MaxPayloadSize, token);
        using (Assert.EnterMultipleScope())
        {
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

    private static async Task<(byte[] Response, TimeSpan Elapsed)> RequestAsync(BeaconP2P server, string protocolId, byte[] wire, bool halfClose, CancellationToken token)
    {
        ServiceProvider services = new ServiceCollection()
            .AddSingleton<RawRequestProtocol>()
            .AddLibp2p(static builder => builder.AddAppLayerProtocol<RawRequestProtocol>())
            .BuildServiceProvider();
        await using (services)
        await using (ILocalPeer requester = services.GetRequiredService<IPeerFactory>().Create(new Identity(privateKey: null, KeyType.Secp256K1)))
        {
            ISession session = await requester.DialAsync(PeerSessionNodes.LoopbackAddress(server), token).WaitAsync(token);
            services.GetRequiredService<RawRequestProtocol>().Id = protocolId;
            services.GetRequiredService<RawRequestProtocol>().HalfClose = halfClose;

            Stopwatch elapsed = Stopwatch.StartNew();
            byte[] response = await session.DialAsync<RawRequestProtocol, byte[], byte[]>(wire, token).WaitAsync(token);
            return (response, elapsed.Elapsed);
        }
    }

    /// <summary>Sends the given bytes as a whole request, optionally half-closes, then reads the response to its end.</summary>
    private sealed class RawRequestProtocol : ISessionProtocol<byte[], byte[]>
    {
        public string Id { get; set; } = "/test/raw-request/1";

        public bool HalfClose { get; set; } = true;

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

            using MemoryStream response = new();
            await stream.CopyToAsync(response, cts.Token);
            return response.ToArray();
        }

        public Task ListenAsync(IChannel downChannel, ISessionContext context) => throw new NotSupportedException();
    }
}
