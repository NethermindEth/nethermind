// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;

namespace Nethermind.Era1.Test;
internal class StreamSegmentTests
{
    [Test]
    public void Constructor_StreamIsNull_ThrowException()
    {
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
        Assert.That(() => new StreamSegment(null, 0, 1), Throws.TypeOf<ArgumentNullException>());
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.
    }
    [Test]
    public void Constructor_StreamIsNotReadable_ThrowException()
    {
        var stream = Substitute.For<Stream>();
        stream.CanRead.Returns(false);

        Assert.That(() => new StreamSegment(stream, 0, 1), Throws.TypeOf<ArgumentException>());
    }
    [TestCase(0, 21)]
    [TestCase(1, 20)]
    [TestCase(11, 10)]
    [TestCase(20, 1)]
    public void Constructor_LengthPlusOffsetIsLongerThanStream_ThrowException(int offset, int length)
    {
        MemoryStream stream = new();
        stream.SetLength(20);

        Assert.That(() => new StreamSegment(stream, offset, length), Throws.TypeOf<ArgumentOutOfRangeException>());
    }
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(10)]
    [TestCase(19)]
    [TestCase(20)]
    public async Task ReadAsync_ReadBeyondTheLength_ReturnsTheSegmentLength(int segmentLength)
    {
        byte[] buffer = new byte[32];
        MemoryStream stream = new();
        stream.SetLength(20);
        StreamSegment sut = new(stream, 0, segmentLength);

        int result = await sut.ReadAsync(buffer, 0, buffer.Length);

        Assert.That(result, Is.EqualTo(segmentLength));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(20)]
    public async Task ReadAsync_ReadLessThanLength_ReturnsTheCount(int count)
    {
        byte[] buffer = new byte[32];
        MemoryStream stream = new();
        stream.SetLength(20);
        StreamSegment sut = new(stream, 0, 20);

        int result = await sut.ReadAsync(buffer, 0, count);

        Assert.That(result, Is.EqualTo(count));
    }

    [Test]
    public async Task ReadAsync_OffsetIsSet_ReadsFromOffset()
    {
        byte[] data = new byte[] {0xff, 0x00, 0x01, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff };
        MemoryStream stream = new(data);
        byte[] buffer = new byte[2];
        StreamSegment sut = new(stream, 1, 3);

        await sut.ReadAsync(buffer, 0, 2);
        
        Assert.That(buffer, Is.EquivalentTo(data[1..3]));
    }

    [Test]
    public async Task ReadAsync_ReadLongerThanSegment_ReadsDataToSegmentLength()
    {
        byte[] data = new byte[] { 0xff, 0xff, 0x00, 0x01, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff };
        MemoryStream stream = new(data);
        byte[] buffer = new byte[32];
        StreamSegment sut = new(stream, 2, 2);

        await sut.ReadAsync(buffer, 0, 32);

        Assert.That(buffer[..2], Is.EquivalentTo(data[2..4]));
    }
}
