// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Execution.Settlement.RollupX;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

public class BlobStreamTests
{
    [Test]
    public void Pack_FirstFieldElement_StoresStreamBytesReversedUnderAZeroTopByte()
    {
        byte[] stream = Enumerable.Range(1, 31).Select(static i => (byte)i).ToArray();

        byte[] blob = BlobStream.Pack(stream).Single();

        Assert.That(blob[0], Is.Zero, "the top byte keeps every field element below the modulus");
        Assert.That(blob[31], Is.EqualTo(1), "stream byte 0 is the least significant byte of the element");
        Assert.That(blob[1], Is.EqualTo(31), "stream byte 30 is the most significant data byte");
        Assert.That(blob.Skip(32).All(static b => b == 0), Is.True, "the rest of the blob is zero");
    }

    [Test]
    public void Unpack_StreamSpanningTwoBlobs_RoundTripsWithZeroPadding()
    {
        byte[] stream = Enumerable.Range(0, BlobStream.DataBytesPerBlob + 100).Select(static i => (byte)(i * 7 + 3)).ToArray();

        byte[][] blobs = BlobStream.Pack(stream);
        byte[] unpacked = BlobStream.Unpack(blobs);

        Assert.That(blobs.Length, Is.EqualTo(2), "one byte past a blob's capacity needs a second blob");
        Assert.That(unpacked.AsSpan(0, stream.Length).ToArray(), Is.EqualTo(stream));
        Assert.That(unpacked.Skip(stream.Length).All(static b => b == 0), Is.True, "the tail is zero padding");
    }

    [Test]
    public void Unpack_FieldElementWithNonZeroTopByte_Throws()
    {
        byte[] blob = new byte[BlobStream.BytesPerBlob];
        blob[BlobStream.BytesPerFieldElement * 5] = 1;

        Assert.Throws<EezSettlementException>(() => BlobStream.Unpack([blob]));
    }

    [TestCase(-1, TestName = "Unpack_ShortDestination_Rejects")]
    [TestCase(1, TestName = "Unpack_LongDestination_Rejects")]
    public void Unpack_DestinationOfWrongSize_Throws(int delta) =>
        Assert.That(() => BlobStream.Unpack(new byte[BlobStream.BytesPerBlob], new byte[BlobStream.DataBytesPerBlob + delta]),
            Throws.TypeOf<ArgumentException>(), "the span caller must supply exactly one logical blob");

    [Test]
    public void Unpack_SecondBlobInvalid_PreservesIndexedError()
    {
        byte[] second = new byte[BlobStream.BytesPerBlob];
        second[32] = 1;
        Assert.That(() => BlobStream.Unpack([new byte[BlobStream.BytesPerBlob], second]),
            Throws.TypeOf<EezSettlementException>().With.Message.Contains("field element 1 of blob 1"), "existing follower errors identify the actual failing blob");
    }

    [Test]
    public void Unpack_BlobOfTheWrongSize_Throws() =>
        Assert.Throws<EezSettlementException>(() => BlobStream.Unpack([new byte[BlobStream.BytesPerBlob - 1]]));
}
