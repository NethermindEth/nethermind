// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.P2P;

public class ReqRespCompressedLengthTests
{
    private const string StreamIdentifier = "0xff060000734e61507059";
    private const string PingFrame = "0x010c00000175de410100000000000000";

    // phase0 p2p ssz_snappy: a reader MUST NOT read more than max_compressed_len(8) = 41 bytes after the length-prefix 8.
    [TestCase(11, false, false, TestName = "request framing of max_compressed_len(n) bytes is read")]
    [TestCase(12, false, true, TestName = "request framing one byte over max_compressed_len(n) is refused")]
    [TestCase(11, true, false, TestName = "response chunk framing of max_compressed_len(n) bytes is read")]
    [TestCase(12, true, true, TestName = "response chunk framing one byte over max_compressed_len(n) is refused")]
    public async Task Framing_past_max_compressed_len_of_the_declared_length_is_refused(int padding, bool response, bool refused)
    {
        byte[] framing = [.. Bytes.FromHexString(StreamIdentifier), 0xfe, (byte)padding, 0, 0, .. new byte[padding], .. Bytes.FromHexString(PingFrame)];
        byte[] wire = response ? [ReqRespFraming.ResponseCode.Success, 0x08, .. framing] : [0x08, .. framing];
        using MemoryStream stream = new(wire);

        Task<byte[]> read = response ? ReadChunkPayloadAsync(stream) : ReqRespFraming.ReadRequestAsync(stream, maxSize: 8, default);

        if (refused)
        {
            Eth2ReqRespException exception = Assert.ThrowsAsync<Eth2ReqRespException>(async () => await read);
            Assert.That(exception.Message, Does.Contain("bound"));
        }
        else
        {
            Assert.That(await read, Is.EqualTo(Bytes.FromHexString("0x0100000000000000")));
        }
    }

    // Neither a frame header (padding 24, 27) nor frame data (padding 28, 40) that would cross max_compressed_len(8) = 41 bytes is read.
    [TestCase(24, false)]
    [TestCase(27, false)]
    [TestCase(28, false)]
    [TestCase(40, false)]
    [TestCase(24, true)]
    [TestCase(27, true)]
    [TestCase(28, true)]
    [TestCase(40, true)]
    public void Frame_past_max_compressed_len_is_not_read(int padding, bool response)
    {
        byte[] framing = [.. Bytes.FromHexString(StreamIdentifier), 0xfe, (byte)padding, 0, 0, .. new byte[padding], .. Bytes.FromHexString(PingFrame)];
        byte[] wire = response ? [ReqRespFraming.ResponseCode.Success, 0x08, .. framing] : [0x08, .. framing];
        int prefixLength = response ? 2 : 1;
        using MemoryStream stream = new(wire);

        Assert.ThrowsAsync<Eth2ReqRespException>(async () => await (response ? ReadChunkPayloadAsync(stream) : ReqRespFraming.ReadRequestAsync(stream, maxSize: 8, default)));
        Assert.That(stream.Position, Is.LessThanOrEqualTo(prefixLength + 41));
    }

    // phase0 p2p ssz_snappy: an empty request reads at most max_compressed_len(0) = 32 bytes after its length-prefix, whatever follows.
    [TestCase(18, false, TestName = "Empty request framing that fills max_compressed_len(0) is read to the bound and no further")]
    [TestCase(15, true, TestName = "Empty request frame header crossing max_compressed_len(0) is refused before it is read")]
    public async Task Empty_request_framing_is_not_read_past_max_compressed_len(int padding, bool refused)
    {
        byte[] wire = [0x00, .. Bytes.FromHexString(StreamIdentifier), 0xfe, (byte)padding, 0, 0, .. new byte[padding], .. Bytes.FromHexString(PingFrame)];
        using MemoryStream stream = new(wire);

        if (refused)
        {
            Assert.ThrowsAsync<Eth2ReqRespException>(() => ReqRespFraming.ReadRequestAsync(stream, maxSize: 8, default, allowEmpty: true));
        }
        else
        {
            Assert.That(await ReqRespFraming.ReadRequestAsync(stream, maxSize: 8, default, allowEmpty: true), Is.Empty);
        }

        Assert.That(stream.Position, Is.LessThanOrEqualTo(1 + 32));
    }

    private static async Task<byte[]> ReadChunkPayloadAsync(Stream stream) =>
        (await ReqRespFraming.ReadResponseChunkAsync(stream, contextBytesLength: 0, maxSize: 8, default))!.Value.Payload;
}
