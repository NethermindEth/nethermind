// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

public class ReqRespFramingTests
{
    // SSZ of ping(1) framed as varint(8) ++ snappy stream identifier ++ one uncompressed frame
    // (type 0x01, length 12 = 4-byte masked CRC32C + 8 data bytes).
    private const string PingSsz = "0x0100000000000000";
    private const string PingRequestWire = "0x08ff060000734e61507059010c00000175de410100000000000000";

    [Test]
    public async Task Encodes_request_to_golden_wire_bytes_and_decodes_back()
    {
        byte[] ssz = Bytes.FromHexString(PingSsz);
        using MemoryStream stream = new();
        await ReqRespFraming.WriteRequestAsync(stream, ssz, default);

        Assert.That(stream.ToArray(), Is.EqualTo(Bytes.FromHexString(PingRequestWire)));

        stream.Position = 0;
        Assert.That(await ReqRespFraming.ReadRequestAsync(stream, maxSize: 8, default), Is.EqualTo(ssz));
    }

    [TestCase(1)]
    [TestCase(84)]
    [TestCase(10_000)]
    [TestCase(200_000)] // Spans multiple 64 KiB snappy frames.
    public async Task Round_trips_request_payloads(int size)
    {
        byte[] ssz = new byte[size];
        new Random(size).NextBytes(ssz);

        using MemoryStream stream = new();
        await ReqRespFraming.WriteRequestAsync(stream, ssz, default);
        stream.Position = 0;

        Assert.That(await ReqRespFraming.ReadRequestAsync(stream, maxSize: size, default), Is.EqualTo(ssz));
    }

    [Test]
    public async Task Round_trips_response_chunk_stream_with_all_result_codes()
    {
        byte[] contextBytes = Bytes.FromHexString("0x01020304");
        byte[] payload = Bytes.FromHexString("0x0102030405060708090a");

        using MemoryStream stream = new();
        await ReqRespFraming.WriteResponseChunkAsync(stream, ReqRespFraming.ResponseCode.Success, contextBytes, payload, default);
        await ReqRespFraming.WriteErrorChunkAsync(stream, ReqRespFraming.ResponseCode.InvalidRequest, "bad", default);
        await ReqRespFraming.WriteErrorChunkAsync(stream, ReqRespFraming.ResponseCode.ServerError, "oops", default);
        await ReqRespFraming.WriteErrorChunkAsync(stream, ReqRespFraming.ResponseCode.ResourceUnavailable, "pruned", default);
        stream.Position = 0;

        AssertChunk(await ReadChunkAsync(stream), ReqRespFraming.ResponseCode.Success, contextBytes, payload);
        AssertChunk(await ReadChunkAsync(stream), ReqRespFraming.ResponseCode.InvalidRequest, [], "bad"u8.ToArray());
        AssertChunk(await ReadChunkAsync(stream), ReqRespFraming.ResponseCode.ServerError, [], "oops"u8.ToArray());
        AssertChunk(await ReadChunkAsync(stream), ReqRespFraming.ResponseCode.ResourceUnavailable, [], "pruned"u8.ToArray());
        Assert.That(await ReadChunkAsync(stream), Is.Null, "end of stream");
    }

    // Truncations of the golden ping request at every interesting boundary: inside the varint-less
    // frame header, inside the magic, inside the data frame header, and inside the frame data.
    [TestCase("0x", 8, Description = "empty stream")]
    [TestCase("0x08", 8, Description = "varint only")]
    [TestCase("0x08ff0600", 8, Description = "cut stream identifier header")]
    [TestCase("0x08ff060000734e", 8, Description = "cut stream identifier magic")]
    [TestCase("0x08ff060000734e61507059010c", 8, Description = "cut data frame header")]
    [TestCase("0x08ff060000734e61507059010c00000175de4101000000", 8, Description = "cut frame data")]
    [TestCase("0x08ff060000734e61507059010c00000175de410100000000000000", 4, Description = "declared length above max")]
    [TestCase("0x04ff060000734e61507059010c00000175de410100000000000000", 8, Description = "frames decode to more than declared")]
    [TestCase("0x08ff060000734e61507059020c00000175de410100000000000000", 8, Description = "unskippable reserved frame type")]
    [TestCase("0x08010c00000175de410100000000000000", 8, Description = "data frame before stream identifier")]
    [TestCase("0x80808080808080808080808001", 8, Description = "varint longer than 10 bytes")]
    public void Rejects_truncated_oversized_and_malformed_requests(string wireHex, int maxSize)
    {
        using MemoryStream stream = new(Bytes.FromHexString(wireHex));
        Assert.ThrowsAsync<Eth2ReqRespException>(() => ReqRespFraming.ReadRequestAsync(stream, maxSize, default));
    }

    private static Task<ResponseChunk?> ReadChunkAsync(Stream stream) =>
        ReqRespFraming.ReadResponseChunkAsync(stream, ReqRespFraming.ForkContextLength, ReqRespFraming.MaxPayloadSize, default);

    private static void AssertChunk(ResponseChunk? actual, byte result, byte[] contextBytes, byte[] payload)
    {
        Assert.That(actual, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual!.Value.Result, Is.EqualTo(result), "result code");
            Assert.That(actual.Value.ContextBytes, Is.EqualTo(contextBytes), "context bytes");
            Assert.That(actual.Value.Payload, Is.EqualTo(payload), "payload");
        }
    }
}
