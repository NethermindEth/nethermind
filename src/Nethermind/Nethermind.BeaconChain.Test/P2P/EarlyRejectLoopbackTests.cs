// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Dto;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// A listener that rejects a request before reading all of it must still answer with an error chunk over a real
/// TCP, noise and yamux session, promptly rather than after its own response timeout.
/// </summary>
public class EarlyRejectLoopbackTests
{
    private const string StatusV2 = "/eth2/beacon_chain/req/status/2/ssz_snappy";
    private const string BlocksByRange = "/eth2/beacon_chain/req/beacon_blocks_by_range/2/ssz_snappy";
    private const string BlocksByRoot = "/eth2/beacon_chain/req/beacon_blocks_by_root/2/ssz_snappy";
    private const string EnvelopesByRange = "/eth2/beacon_chain/req/execution_payload_envelopes_by_range/1/ssz_snappy";
    private const string EnvelopesByRoot = "/eth2/beacon_chain/req/execution_payload_envelopes_by_root/1/ssz_snappy";

    // Well under the listener's 10 s response timeout, so an answer that only comes from that timeout fails here.
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(5);

    public enum Request
    {
        DeclaredLengthAboveTheMaximum,
        VarintLongerThanTenBytes,
        MoreRootsThanTheMaximum,
    }

    [TestCase(StatusV2, Request.DeclaredLengthAboveTheMaximum)]
    [TestCase(StatusV2, Request.VarintLongerThanTenBytes)]
    [TestCase(BlocksByRange, Request.DeclaredLengthAboveTheMaximum)]
    [TestCase(BlocksByRange, Request.VarintLongerThanTenBytes)]
    [TestCase(BlocksByRoot, Request.DeclaredLengthAboveTheMaximum)]
    [TestCase(BlocksByRoot, Request.VarintLongerThanTenBytes)]
    [TestCase(BlocksByRoot, Request.MoreRootsThanTheMaximum)]
    [TestCase(EnvelopesByRange, Request.DeclaredLengthAboveTheMaximum)]
    [TestCase(EnvelopesByRange, Request.VarintLongerThanTenBytes)]
    [TestCase(EnvelopesByRoot, Request.DeclaredLengthAboveTheMaximum)]
    [TestCase(EnvelopesByRoot, Request.VarintLongerThanTenBytes)]
    [TestCase(EnvelopesByRoot, Request.MoreRootsThanTheMaximum)]
    [CancelAfter(60_000)]
    public async Task Early_rejected_requests_are_answered_with_an_error_chunk_at_once(string protocolId, Request request, CancellationToken token)
    {
        byte[] wire = await EncodeAsync(request, token);
        await using BeaconP2P server = PeerSessionNodes.Create().P2P;
        await server.StartAsync(token);
        (byte[] response, TimeSpan elapsed) = await PeerSessionNodes.RetryStalledAsync(attemptToken => RequestAsync(server, protocolId, wire, attemptToken), token);

        using MemoryStream responseStream = new(response);
        ResponseChunk? chunk = await ReqRespFraming.ReadResponseChunkAsync(responseStream, ReqRespFraming.ForkContextLength, ReqRespFraming.MaxPayloadSize, token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunk?.Result, Is.EqualTo(ReqRespFraming.ResponseCode.InvalidRequest), "an error chunk, not silence or a success");
            Assert.That(chunk?.Payload, Is.Not.Empty, "the error chunk carries its message");
            Assert.That(elapsed, Is.LessThan(Prompt), "answered at once, not by the listener's own timeout");
        }
    }

    /// <summary>Sends the request from a fresh plain libp2p peer and returns the whole response and how long it took.</summary>
    private static async Task<(byte[] Response, TimeSpan Elapsed)> RequestAsync(BeaconP2P server, string protocolId, byte[] wire, CancellationToken token)
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

            Stopwatch elapsed = Stopwatch.StartNew();
            byte[] response = await session.DialAsync<RawRequestProtocol, byte[], byte[]>(wire, token).WaitAsync(token);
            return (response, elapsed.Elapsed);
        }
    }

    private static async Task<byte[]> EncodeAsync(Request request, CancellationToken token)
    {
        switch (request)
        {
            case Request.DeclaredLengthAboveTheMaximum:
                using (MemoryStream stream = new())
                {
                    await ReqRespFraming.WriteRequestAsync(stream, new byte[5_000], token);
                    return stream.ToArray();
                }
            case Request.VarintLongerThanTenBytes:
                return Bytes.FromHexString("0x8080808080808080808001");
            case Request.MoreRootsThanTheMaximum:
                using (MemoryStream stream = new())
                {
                    await ReqRespFraming.WriteRequestAsync(stream, new byte[129 * Hash256.Size], token);
                    return stream.ToArray();
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    /// <summary>Sends the given bytes as a whole request under whichever protocol id it is set to, then reads the response to its end.</summary>
    private sealed class RawRequestProtocol : ISessionProtocol<byte[], byte[]>
    {
        public string Id { get; set; } = "/test/raw-request/1";

        public async Task<byte[]> DialAsync(IChannel downChannel, ISessionContext context, byte[] request)
        {
            using CancellationTokenSource cts = new(Prompt + Prompt);
            ChannelStreamAdapter stream = new(downChannel);
            await stream.WriteAsync(request, cts.Token);
            await downChannel.WriteEofAsync(cts.Token);
            using MemoryStream response = new();
            await stream.CopyToAsync(response, cts.Token);
            return response.ToArray();
        }

        public Task ListenAsync(IChannel downChannel, ISessionContext context) => throw new NotSupportedException();
    }
}
