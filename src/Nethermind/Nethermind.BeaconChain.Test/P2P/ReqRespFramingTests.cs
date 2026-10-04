// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.Core.Extensions;
using Nethermind.Libp2p.Core;
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

    [Test]
    public async Task Empty_error_preserves_the_peer_result([Values("0x0300", "0x0300ff060000734e6150705901040000d8ea82a2")] string wire)
    {
        using MemoryStream input = new(Bytes.FromHexString(wire));
        AssertChunk(await ReqRespFraming.ReadResponseChunkAsync(input, 4, 8, default), ReqRespFraming.ResponseCode.ResourceUnavailable, [], []);
    }

    [Test]
    public void Rejects_overflowing_tenth_varint_byte([Values(2, 127, 128, 255)] byte last, [Values] bool response)
    {
        byte[] prefix = [0x88, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, last];
        byte[] framed = Bytes.FromHexString(PingRequestWire)[1..];
        using MemoryStream input = new(response ? [0, .. prefix, .. framed] : [.. prefix, .. framed]);
        Eth2ReqRespException thrown = Assert.ThrowsAsync<Eth2ReqRespException>(async () =>
        {
            if (response) await ReqRespFraming.ReadResponseChunkAsync(input, 0, 8, default);
            else await ReqRespFraming.ReadRequestAsync(input, 8, default);
        })!;
        Assert.That(thrown.Message, Does.Contain("overflows Uint64"));
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
    [TestCase("0x08ff060000734e61507059010c00000176de410100000000000000", 8, Description = "data frame with a bad CRC")]
    [TestCase("0x80808080808080808080808001", 8, Description = "varint longer than 10 bytes")]
    public void Rejects_truncated_oversized_and_malformed_requests(string wireHex, int maxSize)
    {
        using MemoryStream stream = new(Bytes.FromHexString(wireHex));
        Assert.ThrowsAsync<Eth2ReqRespException>(() => ReqRespFraming.ReadRequestAsync(stream, maxSize, default));
    }

    // SSZ of an empty list is zero bytes; any snappy framing that decodes to no data is legal for it.
    // An empty data frame carries the masked CRC32C of nothing, 0xa282ead8.
    [TestCase("0x00", false, Description = "empty list without framing")]
    [TestCase("0x00ff060000734e61507059", false, Description = "empty list with its stream identifier")]
    [TestCase("0x00ff060000734e61507059ff060000734e61507059", false, Description = "a repeated stream identifier")]
    [TestCase("0x00ff060000734e6150705900050000d8ea82a200", false, Description = "an empty compressed frame")]
    [TestCase("0x00ff060000734e6150705901040000d8ea82a2", false, Description = "an empty uncompressed frame")]
    [TestCase("0x00ff060000734e61507059fe030000000000", false, Description = "a padding frame")]
    [TestCase("0x00ff060000734e6150705900060000d8ea82a28000", false, Description = "an empty compressed block with a non-canonical zero length")]
    [TestCase("0x00ff060000734e6150705900090000d8ea82a28080808000", false, Description = "an empty compressed block with a five byte zero length")]
    [TestCase("0x00ff060000734e6150705980000000", false, Description = "an empty skippable frame")]
    [TestCase("0x00ff060000734e61507059fe03000000000000050000d8ea82a200", false, Description = "padding then an empty compressed frame")]
    [TestCase("0x00ff060000734e6150705900050000d8ea82a200fe030000000000", true, Description = "a padding frame after the data frame")]
    [TestCase("0x00ff060000734e6150705900050000d8ea82a20000050000d8ea82a200", true, Description = "a second data frame")]
    [TestCase("0x00ff060000734e6150705900050000d8ea82a200fe", true, Description = "a lone frame type byte after the data frame")]
    [TestCase("0x00ff060000734e6150705900", true, Description = "a byte after the stream identifier")]
    [TestCase("0x00010c00000175de410100000000000000", true, Description = "a data frame")]
    [TestCase("0x00ff060000734e61507059010c00000175de410100000000000000", true, Description = "a data frame after the stream identifier")]
    [TestCase("0x00ff060000734e6150705901050000d8ea82a201", true, Description = "a data frame carrying the empty-data CRC")]
    [TestCase("0x00ff060000734e6150705900050000d8ea82a300", true, Description = "an empty compressed frame with a bad CRC")]
    [TestCase("0x00ff060000734e6150705901040000d8ea82a3", true, Description = "an empty uncompressed frame with a bad CRC")]
    [TestCase("0x00fe030000000000ff060000734e61507059", true, Description = "padding before the stream identifier")]
    [TestCase("0x00ff060000734e6150705902000000", true, Description = "an unskippable reserved frame")]
    [TestCase("0x00ff060000734e6150705901080000d8ea82a2ab9be09b", true, Description = "an uncompressed frame whose data has the empty-data CRC")]
    [TestCase("0x00ff060000734e61507059000a0000d8ea82a2040cab9be09b", true, Description = "a compressed frame whose data has the empty-data CRC")]
    [TestCase("0x00ff060000734e6150705900060000d8ea82a20000", true, Description = "an empty compressed block with a trailing byte")]
    [TestCase("0x00ff060000734e6150705900050000d8ea82a201", true, Description = "a compressed block declaring a byte it does not carry")]
    [TestCase("0x00ff060000734e6150705900050000d8ea82a280", true, Description = "a compressed block whose length varint never ends")]
    [TestCase("0x00ff060000734e6150705900060000d8ea82a28001", true, Description = "a non-canonical length varint that is not zero")]
    [TestCase("0x00ff060000734e6150705900060000d8ea82a28100", true, Description = "a non-canonical length of one with no byte")]
    [TestCase("0x00ff060000734e6150705900070000d8ea82a2800000", true, Description = "a non-canonical zero length with a trailing byte")]
    [TestCase("0x00ff060000734e61507059000a0000d8ea82a2808080808000", true, Description = "a zero length varint longer than five bytes")]
    [TestCase("0x00ff060000734e61507058", true, Description = "a stream identifier with the wrong content")]
    [TestCase("0x00ff000000", true, Description = "an empty stream identifier")]
    [TestCase("0x00ff060000734e", true, Description = "cut stream identifier")]
    [TestCase("0x00ff060000734e61507059fe03", true, Description = "a cut frame header after the stream identifier")]
    [TestCase("0x00ff060000734e61507059fe03000000", true, Description = "a cut padding frame")]
    [TestCase("0x00ff060000734e6150705901000000", true, Description = "an uncompressed frame shorter than its checksum")]
    [TestCase("0x00ff060000734e6150705900050000d8ea82", true, Description = "a cut empty compressed frame")]
    public async Task Zero_length_request_is_read_as_empty_only_where_the_type_allows_it(string wireHex, bool malformed)
    {
        using MemoryStream strict = new(Bytes.FromHexString(wireHex));
        Assert.ThrowsAsync<Eth2ReqRespException>(() => ReqRespFraming.ReadRequestAsync(strict, maxSize: 8, default), "a type with a nonzero minimum size");

        using MemoryStream list = new(Bytes.FromHexString(wireHex));
        if (malformed)
        {
            Assert.ThrowsAsync<Eth2ReqRespException>(() => ReqRespFraming.ReadRequestAsync(list, maxSize: 8, default, allowEmpty: true));
        }
        else
        {
            Assert.That(await ReqRespFraming.ReadRequestAsync(list, maxSize: 8, default, allowEmpty: true), Is.Empty);
        }
    }

    // A listener serves an empty request once its zero length prefix is in; frames the requester still owes are legal, anything else that follows is a violation.
    [TestCase("0x", null, Description = "the requester ends its stream")]
    [TestCase("0xff060000734e61507059", null, Description = "a stream identifier still owed")]
    [TestCase("0xff060000734e6150705900050000d8ea82a200", null, Description = "the empty data frame still owed")]
    [TestCase("0xff060000734e61507059fe030000000000", null, Description = "a padding frame")]
    [TestCase("0xff060000734e6150705900050000d8ea82a200fe", "Unexpected bytes", Description = "a byte after the data frame")]
    [TestCase("0xff060000734e61507059fe09000000000000000000000000050000d8ea82a2002a", null, Description = "framing that fills max_compressed_len(0) exactly is not read past")]
    [TestCase("0x01040000d8ea82a2", "stream identifier", Description = "a data frame before the stream identifier")]
    [TestCase("0xff060000734e61507058", "identifier", Description = "a stream identifier with the wrong content")]
    [TestCase("0xff060000734e", "Truncated", Description = "a cut frame")]
    public async Task Bytes_after_the_prefix_of_an_empty_request_are_owed_framing_or_a_violation(string lateHex, string? violation)
    {
        using MemoryStream head = new(Bytes.FromHexString("0x00"));
        (_, ReqRespFraming.RequestTail tail) = await ReqRespFraming.ReadRequestWithTailAsync(head, maxSize: 8, default, allowEmpty: true);

        using MemoryStream late = new(Bytes.FromHexString(lateHex));
        string? result = await tail.WatchAsync(late, default);

        if (violation is null)
        {
            Assert.That(result, Is.Null);
        }
        else
        {
            Assert.That(result, Does.Contain(violation));
        }
    }

    [TestCase("0x", false)]
    [TestCase("0x2a", true)]
    public async Task Bytes_after_a_request_payload_are_a_violation_and_the_end_of_the_stream_is_not(string lateHex, bool violation)
    {
        using MemoryStream head = new(Bytes.FromHexString(PingRequestWire));
        (byte[] payload, ReqRespFraming.RequestTail tail) = await ReqRespFraming.ReadRequestWithTailAsync(head, maxSize: 8, default);
        using MemoryStream late = new(Bytes.FromHexString(lateHex));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(payload, Is.EqualTo(Bytes.FromHexString(PingSsz)));
        Assert.That(await tail.WatchAsync(late, default), violation ? Is.Not.Null : Is.Null);
    }

    // A peer that repeats the stream-identifier frame forever never advances uncompressedTotal (the
    // loop's only exit condition before the fix), so it would buffer without bound. The 20,000
    // repeats here supply far more than the compressed-size bound for an 8-byte payload; asserting
    // the stream position stops well short of the end proves the read aborts on the bound rather
    // than merely hitting end of stream once the (attacker-controlled) input runs out.
    [Test]
    public void Rejects_endless_stream_identifier_frames_before_exhausting_input()
    {
        byte[] streamIdentifierFrame = Bytes.FromHexString("0xff060000734e61507059");
        using MemoryStream stream = new();
        stream.WriteByte(0x08); // varint(8): declared payload length
        for (int i = 0; i < 20_000; i++)
        {
            stream.Write(streamIdentifierFrame);
        }

        stream.Position = 0;

        Eth2ReqRespException exception = Assert.ThrowsAsync<Eth2ReqRespException>(
            () => ReqRespFraming.ReadRequestAsync(stream, maxSize: 8, default));
        Assert.That(exception.Message, Does.Contain("bound"));
        Assert.That(stream.Position, Is.LessThan(stream.Length));
    }

    // Frames that decode to nothing never end an empty request on their own, so only the byte bound stops a peer that keeps sending them.
    [TestCase("0xff060000734e61507059", Description = "repeated stream identifiers")]
    [TestCase("0xfe030000000000", Description = "repeated padding frames")]
    public void Rejects_endless_empty_frames_after_an_empty_request_before_exhausting_input(string repeatedFrameHex)
    {
        byte[] repeatedFrame = Bytes.FromHexString(repeatedFrameHex);
        using MemoryStream stream = new();
        stream.Write(Bytes.FromHexString("0x00ff060000734e61507059"));
        for (int i = 0; i < 1_000; i++)
        {
            stream.Write(repeatedFrame);
        }

        stream.Position = 0;

        Eth2ReqRespException exception = Assert.ThrowsAsync<Eth2ReqRespException>(
            () => ReqRespFraming.ReadRequestAsync(stream, maxSize: 8, default, allowEmpty: true));
        Assert.That(exception.Message, Does.Contain("bound"));
        Assert.That(stream.Position, Is.LessThan(128), "the read stops at the bound, not at the end of the input");
    }

    // A 10-byte stream identifier plus a padding frame of 4 header bytes and this much data: max_compressed_len(0) = 32 bytes is the most an empty request reads.
    [TestCase(18, false)]
    [TestCase(19, true)]
    public async Task Empty_request_framing_is_read_up_to_its_byte_bound(int paddingLength, bool rejected)
    {
        byte[] wire = [.. Bytes.FromHexString("0x00ff060000734e61507059"), 0xfe, (byte)paddingLength, 0, 0, .. new byte[paddingLength]];
        using MemoryStream stream = new(wire);
        if (rejected)
        {
            Eth2ReqRespException exception = Assert.ThrowsAsync<Eth2ReqRespException>(() => ReqRespFraming.ReadRequestAsync(stream, maxSize: 8, default, allowEmpty: true));
            Assert.That(exception.Message, Does.Contain("bound"));
        }
        else
        {
            Assert.That(await ReqRespFraming.ReadRequestAsync(stream, maxSize: 8, default, allowEmpty: true), Is.Empty);
        }
    }

    // Stream identifier 10 + padding frame 15 + uncompressed data frame 16 = max_compressed_len(8) = 41 bytes: the bound is used up, so the next byte is not read.
    [Test]
    public async Task A_request_whose_framing_uses_the_whole_bound_is_not_probed_for_more_bytes()
    {
        byte[] wire = [.. Bytes.FromHexString("0x08ff060000734e61507059"), 0xfe, 11, 0, 0, .. new byte[11], .. Bytes.FromHexString("0x010c00000175de410100000000000000"), 0x2a];
        using MemoryStream stream = new(wire);

        Assert.That(await ReqRespFraming.ReadRequestAsync(stream, maxSize: 8, default), Is.EqualTo(Bytes.FromHexString(PingSsz)));
        Assert.That(stream.Position, Is.EqualTo(wire.Length - 1), "the byte past max_compressed_len(n) stays unread");
    }

    [Test]
    public async Task Complete_request_on_an_open_channel_returns_without_waiting_for_more_bytes()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        Channel channel = new();
        using ChannelStreamAdapter input = new(channel);
        using ChannelStreamAdapter output = new(channel.Reverse);
        Task<byte[]> read = ReqRespFraming.ReadRequestAsync(input, maxSize: 8, deadline.Token);
        try
        {
            await output.WriteAsync(Bytes.FromHexString(PingRequestWire), deadline.Token);
            Assert.That(await read.WaitAsync(deadline.Token), Is.EqualTo(Bytes.FromHexString(PingSsz)));
        }
        finally
        {
            deadline.Cancel();
            await channel.CloseAsync();
        }
    }

    private static Task<ResponseChunk?> ReadChunkAsync(Stream stream) =>
        ReqRespFraming.ReadResponseChunkAsync(stream, ReqRespFraming.ForkContextLength, ReqRespFraming.MaxPayloadSize, default);

    private static void AssertChunk(ResponseChunk? actual, byte result, byte[] contextBytes, byte[] payload)
    {
        Assert.That(actual, Is.Not.Null);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(actual!.Value.Result, Is.EqualTo(result), "result code");
        Assert.That(actual.Value.ContextBytes, Is.EqualTo(contextBytes), "context bytes");
        Assert.That(actual.Value.Payload, Is.EqualTo(payload), "payload");
    }
}
