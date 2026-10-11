// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.IO;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.BeaconChain.Test.P2P;

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

    private static IEnumerable<TestCaseData> Requests()
    {
        foreach (string protocol in new[] { StatusV2, BlocksByRange, BlocksByRoot, EnvelopesByRange, EnvelopesByRoot, ColumnsByRange, ColumnsByRoot, Ping, Goodbye })
            foreach (Request request in Enum.GetValues<Request>())
            {
                bool byRoot = protocol is BlocksByRoot or EnvelopesByRoot or ColumnsByRoot;
                bool supported = request switch
                {
                    Request.MoreRootsThanTheMaximum => byRoot,
                    Request.ZeroLengthStreamHeldOpen => !byRoot && protocol != Goodbye,
                    Request.TruncatedAfterTheLengthPrefix => protocol is StatusV2 or BlocksByRoot or ColumnsByRoot or Goodbye,
                    Request.EmptyColumnList => protocol is ColumnsByRange or ColumnsByRoot,
                    _ => true
                };
                if (!supported) continue;
                bool empty = byRoot && request == Request.ZeroLength;
                string name = empty ? "Empty_by_root_lists_close_the_stream_at_once_with_no_chunk"
                    : "Early_rejected_requests_are_answered_with_an_error_chunk_at_once";
                yield return new TestCaseData(protocol, request, empty).SetName($"{name}(\"{protocol}\",{request})");
            }
    }

    [TestCaseSource(nameof(Requests))]
    [CancelAfter(60_000)]
    public async Task Early_rejected_requests_are_answered_at_once(string protocolId, Request request, bool empty, CancellationToken token)
    {
        byte[] wire = await EncodeAsync(protocolId, request, token);
        await using BeaconP2P server = PeerSessionNodes.Create().P2P;
        await server.StartAsync(token);
        (byte[] response, TimeSpan elapsed) = await ReqResp.TrailingRequestBytesLoopbackTests.RequestAsync(server, protocolId, wire, request != Request.ZeroLengthStreamHeldOpen, token, logManager: LoopbackTrace.Or(LimboLogs.Instance, "requester"));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        if (empty)
        {
            Assert.That(response, Is.Empty, "no chunk, error or success");
        }
        else
        {
            using MemoryStream responseStream = new(response);
            ResponseChunk? chunk = await ReqRespFraming.ReadResponseChunkAsync(responseStream, ReqRespFraming.ForkContextLength, ReqRespFraming.MaxPayloadSize, token);
            Assert.That(chunk, Is.Not.Null, "the peer sends an error chunk");
            Assert.That(chunk?.Result, Is.EqualTo(ReqRespFraming.ResponseCode.InvalidRequest), "an error chunk, not silence or a success");
            Assert.That(chunk?.Payload, Is.Not.Empty, "the error chunk carries its message");
        }
        Assert.That(elapsed, Is.LessThan(Prompt), "answered or closed at once, not by the listener's own timeout");
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
}
