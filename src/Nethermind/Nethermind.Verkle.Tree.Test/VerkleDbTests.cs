using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Extensions;
using NUnit.Framework;

namespace Nethermind.Verkle.Tree.Test;

public class VerkleDbTests
{
    [Test]
    public void ByteArrayEqualityTestsDictionary()
    {
        byte[] a = { 1, 2 };
        byte[] b = { 1, 2 };

        Dictionary<byte[], byte[]> table = new Dictionary<byte[], byte[]>
        {
            [a] = b,
        };
        table.TryGetValue(b, out byte[] _).Should().BeFalse();

        table = new Dictionary<byte[], byte[]>(Bytes.EqualityComparer)
        {
            [a] = b,
        };
        table.TryGetValue(b, out byte[] _).Should().BeTrue();
    }

    // [Test]
    // public void TestDiffLayer()
    // {
    //     DiffLayer forwardDiff = new DiffLayer(DiffType.Forward);
    //     DiffLayer reverseDiff = new DiffLayer(DiffType.Reverse);
    //
    //     MemoryStateDb currentState = new MemoryStateDb();
    //     MemoryStateDb changes = new MemoryStateDb();
    //
    //
    // }
}
