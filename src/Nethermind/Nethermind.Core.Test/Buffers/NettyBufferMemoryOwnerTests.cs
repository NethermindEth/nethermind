// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Core.Buffers;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Buffers;

public class NettyBufferMemoryOwnerTests
{
    [Test]
    public void Should_ExposeBufferMemory()
    {
        IByteBuffer buffer = Unpooled.Buffer(10);
        buffer.SetWriterIndex(buffer.WriterIndex + 10);
        buffer.AsSpan().Fill(0);
        NettyBufferMemoryOwner memoryOwner = new NettyBufferMemoryOwner(buffer);
        memoryOwner.Memory.Length.Should().Be(10);
        memoryOwner.Memory.Span.Fill(1);

        buffer.AsSpan().ToArray().Should().BeEquivalentTo(Enumerable.Repeat((byte)1, 10).ToArray());
    }

    [Test]
    public void When_Dispose_ShouldReduceBufferRefCounter()
    {
        IByteBuffer buffer = Unpooled.Buffer(10);
        buffer.SetWriterIndex(buffer.WriterIndex + 10);
        buffer.ReferenceCount.Should().Be(1);

        NettyBufferMemoryOwner memoryOwner = new NettyBufferMemoryOwner(buffer);
        memoryOwner.Memory.Length.Should().Be(10);
        buffer.ReferenceCount.Should().Be(2);

        memoryOwner.Dispose();
        buffer.ReferenceCount.Should().Be(1);
    }
}
