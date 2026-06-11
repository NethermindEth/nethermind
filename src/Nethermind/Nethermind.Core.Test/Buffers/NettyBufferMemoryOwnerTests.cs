// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using DotNetty.Buffers;
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
        buffer.AsSpan().Clear();
        NettyBufferMemoryOwner memoryOwner = new(buffer);
        Assert.That(memoryOwner.Memory.Length, Is.EqualTo(10));
        memoryOwner.Memory.Span.Fill(1);

        Assert.That(buffer.AsSpan().ToArray(), Is.EqualTo(Enumerable.Repeat((byte)1, 10).ToArray()));
    }

    [Test]
    public void When_Dispose_ShouldReduceBufferRefCounter()
    {
        IByteBuffer buffer = Unpooled.Buffer(10);
        buffer.SetWriterIndex(buffer.WriterIndex + 10);
        Assert.That(buffer.ReferenceCount, Is.EqualTo(1));

        NettyBufferMemoryOwner memoryOwner = new(buffer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(memoryOwner.Memory.Length, Is.EqualTo(10));
            Assert.That(buffer.ReferenceCount, Is.EqualTo(2));
        }

        memoryOwner.Dispose();
        Assert.That(buffer.ReferenceCount, Is.EqualTo(1));
    }
}
