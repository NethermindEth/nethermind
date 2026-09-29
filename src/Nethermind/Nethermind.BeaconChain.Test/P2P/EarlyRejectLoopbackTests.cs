// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.Types;
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
    private const string ColumnsByRange = "/eth2/beacon_chain/req/data_column_sidecars_by_range/1/ssz_snappy";
    private const string ColumnsByRoot = "/eth2/beacon_chain/req/data_column_sidecars_by_root/1/ssz_snappy";
    private const string Ping = "/eth2/beacon_chain/req/ping/1/ssz_snappy";
    private const string Goodbye = "/eth2/beacon_chain/req/goodbye/1/ssz_snappy";

    // Well under the listener's 10 s response timeout, so an answer that only comes from that timeout fails here.
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(5);

    public enum Request
    {
        DeclaredLengthAboveTheMaximum,
        VarintLongerThanTenBytes,
        MoreRootsThanTheMaximum,
        ZeroLength,
        ZeroLengthStreamHeldOpen,
        TruncatedAfterTheLengthPrefix,
        EmptyColumnList,
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
    [TestCase(ColumnsByRange, Request.DeclaredLengthAboveTheMaximum)]
    [TestCase(ColumnsByRange, Request.VarintLongerThanTenBytes)]
    [TestCase(ColumnsByRoot, Request.DeclaredLengthAboveTheMaximum)]
    [TestCase(ColumnsByRoot, Request.VarintLongerThanTenBytes)]
    [TestCase(ColumnsByRoot, Request.MoreRootsThanTheMaximum)]
    [TestCase(Ping, Request.DeclaredLengthAboveTheMaximum)]
    [TestCase(Goodbye, Request.DeclaredLengthAboveTheMaximum)]
    [TestCase(Ping, Request.VarintLongerThanTenBytes)]
    [TestCase(Goodbye, Request.VarintLongerThanTenBytes)]
    // A zero length prefix is refused where the request type has a nonzero minimum size.
    [TestCase(StatusV2, Request.ZeroLength)]
    [TestCase(BlocksByRange, Request.ZeroLength)]
    [TestCase(EnvelopesByRange, Request.ZeroLength)]
    [TestCase(ColumnsByRange, Request.ZeroLength)]
    [TestCase(Ping, Request.ZeroLength)]
    [TestCase(Goodbye, Request.ZeroLength)]
    // The requester sends the zero prefix and keeps the stream open: a fixed-size request needs no more framing, so the refusal must not wait for a half-close.
    [TestCase(StatusV2, Request.ZeroLengthStreamHeldOpen)]
    [TestCase(BlocksByRange, Request.ZeroLengthStreamHeldOpen)]
    [TestCase(EnvelopesByRange, Request.ZeroLengthStreamHeldOpen)]
    [TestCase(ColumnsByRange, Request.ZeroLengthStreamHeldOpen)]
    [TestCase(Ping, Request.ZeroLengthStreamHeldOpen)]
    [TestCase(ColumnsByRange, Request.EmptyColumnList)]
    [TestCase(ColumnsByRoot, Request.EmptyColumnList)]
    [TestCase(StatusV2, Request.TruncatedAfterTheLengthPrefix)]
    [TestCase(BlocksByRoot, Request.TruncatedAfterTheLengthPrefix)]
    [TestCase(ColumnsByRoot, Request.TruncatedAfterTheLengthPrefix)]
    [TestCase(Goodbye, Request.TruncatedAfterTheLengthPrefix)]
    [CancelAfter(60_000)]
    public async Task Early_rejected_requests_are_answered_with_an_error_chunk_at_once(string protocolId, Request request, CancellationToken token)
    {
        byte[] wire = await EncodeAsync(protocolId, request, token);
        await using BeaconP2P server = PeerSessionNodes.Create().P2P;
        await server.StartAsync(token);
        (byte[] response, TimeSpan elapsed) = await PeerSessionNodes.RetryStalledAsync(attemptToken => RequestAsync(server, protocolId, wire, request != Request.ZeroLengthStreamHeldOpen, attemptToken), token);

        using MemoryStream responseStream = new(response);
        ResponseChunk? chunk = await ReqRespFraming.ReadResponseChunkAsync(responseStream, ReqRespFraming.ForkContextLength, ReqRespFraming.MaxPayloadSize, token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunk?.Result, Is.EqualTo(ReqRespFraming.ResponseCode.InvalidRequest), "an error chunk, not silence or a success");
            Assert.That(chunk?.Payload, Is.Not.Empty, "the error chunk carries its message");
            Assert.That(elapsed, Is.LessThan(Prompt), "answered at once, not by the listener's own timeout");
        }
    }

    // A list that may be empty has nothing to send, so it must close the stream at once.
    [TestCase(BlocksByRoot, Request.ZeroLength)]
    [TestCase(EnvelopesByRoot, Request.ZeroLength)]
    [TestCase(ColumnsByRoot, Request.ZeroLength)]
    [CancelAfter(60_000)]
    public async Task Empty_by_root_lists_close_the_stream_at_once_with_no_chunk(string protocolId, Request request, CancellationToken token)
    {
        byte[] wire = await EncodeAsync(protocolId, request, token);
        await using BeaconP2P server = PeerSessionNodes.Create().P2P;
        await server.StartAsync(token);
        (byte[] response, TimeSpan elapsed) = await PeerSessionNodes.RetryStalledAsync(attemptToken => RequestAsync(server, protocolId, wire, request != Request.ZeroLengthStreamHeldOpen, attemptToken), token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response, Is.Empty, "no chunk, error or success");
            Assert.That(elapsed, Is.LessThan(Prompt), "closed at once, not by the listener's own timeout");
        }
    }

    /// <summary>Sends the request from a fresh plain libp2p peer and returns the whole response and how long it took.</summary>
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

    /// <summary>Encodes a by-root list by hand: the generated encoder refuses more identifiers than the spec limit.</summary>
    private static byte[] EncodeIdentifierList(int count, ulong[] columns)
    {
        byte[] identifier = DataColumnsByRootIdentifier.Encode(new DataColumnsByRootIdentifier { BlockRoot = Hash256.Zero, Columns = columns });
        byte[] list = new byte[count * (sizeof(uint) + identifier.Length)];
        for (int i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(list.AsSpan(i * sizeof(uint)), (uint)(count * sizeof(uint) + i * identifier.Length));
            identifier.CopyTo(list, count * sizeof(uint) + i * identifier.Length);
        }

        return list;
    }

    private static async Task<byte[]> EncodeAsync(string protocolId, Request request, CancellationToken token)
    {
        switch (request)
        {
            case Request.DeclaredLengthAboveTheMaximum:
                // Above every request maximum, the widest being the column by-root list.
                using (MemoryStream stream = new())
                {
                    await ReqRespFraming.WriteRequestAsync(stream, new byte[200_000], token);
                    return stream.ToArray();
                }
            case Request.VarintLongerThanTenBytes:
                return Bytes.FromHexString("0x8080808080808080808001");
            case Request.MoreRootsThanTheMaximum:
                using (MemoryStream stream = new())
                {
                    byte[] ssz = protocolId == ColumnsByRoot ? EncodeIdentifierList(129, columns: [0UL]) : new byte[129 * Hash256.Size];
                    await ReqRespFraming.WriteRequestAsync(stream, ssz, token);
                    return stream.ToArray();
                }
            case Request.ZeroLength or Request.ZeroLengthStreamHeldOpen:
                return [0x00];
            case Request.TruncatedAfterTheLengthPrefix:
                return [0x01];
            case Request.EmptyColumnList:
                using (MemoryStream stream = new())
                {
                    byte[] ssz = protocolId == ColumnsByRoot
                        ? EncodeIdentifierList(1, columns: [])
                        : DataColumnSidecarsByRangeRequest.Encode(new DataColumnSidecarsByRangeRequest { StartSlot = 0, Count = 1, Columns = [] });
                    await ReqRespFraming.WriteRequestAsync(stream, ssz, token);
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

        public bool HalfClose { get; set; } = true;

        public async Task<byte[]> DialAsync(IChannel downChannel, ISessionContext context, byte[] request)
        {
            using CancellationTokenSource cts = new(Prompt + Prompt);
            ChannelStreamAdapter stream = new(downChannel);
            await stream.WriteAsync(request, cts.Token);
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
