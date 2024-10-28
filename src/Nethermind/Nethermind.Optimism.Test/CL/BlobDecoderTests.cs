// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.IO;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Optimism.CL;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class BlobDecoderTests
{
    private static byte[] StringToByteArray(string hex) {
        return Enumerable.Range(0, hex.Length)
            .Where(x => x % 2 == 0)
            .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
            .ToArray();
    }

    [TestCase(994040)]
    [TestCase(2243081)]
    [TestCase(35443070)]
    [TestCase(1484649)]
    [TestCase(76888454)]
    [TestCase(69007144)] // isLast = false
    public void Blob_decode_test(int index)
    {
        StreamReader inputReader = new StreamReader($"/home/deffrian/Documents/testvectors/input{index}");
        StreamReader outputReader = new StreamReader($"/home/deffrian/Documents/testvectors/output{index}");
        byte[] blob = StringToByteArray(inputReader.ReadToEnd());
        byte[] decoded = StringToByteArray(outputReader.ReadToEnd());
        inputReader.Close();
        outputReader.Close();
        byte[] result = BlobDecoder.DecodeBlob(new BlobSidecar { Blob = blob });
        Assert.That(decoded, Is.EqualTo(result));

        var frames = FrameDecoder.DecodeFrames(result);
        Assert.That(frames.Length, Is.EqualTo(1));
        Assert.That(frames[0].IsLast, Is.True);

        var end = ChannelDecoder.DecodeChannel(frames[0]);
    }

    [TestCase(12123921)]
    public void Vectors(int index)
    {
        StreamReader inputReader = new StreamReader($"/home/deffrian/Documents/vectors/input{index}");
        StreamReader outputReader = new StreamReader($"/home/deffrian/Documents/vectors/output{index}");
        byte[] blob = StringToByteArray(inputReader.ReadToEnd());
        byte[] decoded = StringToByteArray(outputReader.ReadToEnd());
        inputReader.Close();
        outputReader.Close();
        byte[] result = BlobDecoder.DecodeBlob(new BlobSidecar { Blob = blob });
        Assert.That(decoded, Is.EqualTo(result));

        var frames = FrameDecoder.DecodeFrames(result);
        Assert.That(frames.Length, Is.EqualTo(1));
        Assert.That(frames[0].IsLast, Is.True);

        var end = ChannelDecoder.DecodeChannel(frames[0]);
    }

    [Test]
    public void Another_test()
    {
        StreamReader inputReader = new StreamReader($"/home/deffrian/Documents/vectors/frames/input47623837");
        StreamReader outputReader = new StreamReader($"/home/deffrian/Documents/vectors/frames/data47623837");
        byte[] input = StringToByteArray(inputReader.ReadToEnd());
        byte[] data = StringToByteArray(outputReader.ReadToEnd());
        inputReader.Close();
        outputReader.Close();

        var frames = FrameDecoder.DecodeFrames(input);
        Assert.That(frames.Length, Is.EqualTo(1));
        //Assert.That(frames[0].IsLast, Is.True);

        Assert.That(frames[0].FrameData, Is.EquivalentTo(data));

        var end = ChannelDecoder.DecodeChannel(frames[0]);
    }

    [Test]
    public void Another_test2()
    {
        StreamReader inputReader = new StreamReader($"/home/deffrian/Documents/testvectors/channels/input125142");
        StreamReader outputReader = new StreamReader($"/home/deffrian/Documents/testvectors/channels/decompressed125142");
        byte[] input = StringToByteArray(inputReader.ReadToEnd());
        byte[] data = StringToByteArray(outputReader.ReadToEnd());
        inputReader.Close();
        outputReader.Close();

        // BatchV1 decoded = BatchDecoder.Instance.DecodeSpanBatch(input)
        BatchV1 batch = BatchDecoder.Instance.DecodeSpanBinary(data);
        // (BatchV1[] batches, byte[] decompressed)  = ChannelDecoder.DecodeChannel(new Frame(){FrameData = input});
        // Assert.That(batches, Is.EqualTo(1));
        //Assert.That(frames[0].IsLast, Is.True);

        // Assert.That(decompressed, Is.EquivalentTo(data));

        // var end = ChannelDecoder.DecodeChannel(frames[0]);
    }

    public static IEnumerable BlobTestCases
    {
        get
        {
            yield return new TestCaseData(
                Bytes.FromHexString(""),
                Bytes.FromHexString("")
            )
            {
                TestName = "Sepolia blob 1"
            };
        }
    }
}
