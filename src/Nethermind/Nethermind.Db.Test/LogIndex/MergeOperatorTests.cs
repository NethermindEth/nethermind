// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.InteropServices;
using ConcurrentCollections;
using MathNet.Numerics.Random;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Db.LogIndex;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Db.Test.LogIndex;

// TODO: more tests
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class MergeOperatorTests
{
    [TestCase(
        null,
        new[] { "1, 2", "3", "4" },
        "1, 2, 3, 4"
    )]
    [TestCase(
        "1",
        new[] { "2, 3", "4" },
        "1, 2, 3, 4"
    )]
    [TestCase(
        "1, 2",
        new[] { "3, 4", "Reorg:3", "5, 6" },
        "1, 2, 5, 6"
    )]
    [TestCase(
        "1, 2, 3",
        new[] { "4, 5", "Truncate:4", "6, 7" },
        "5, 6, 7"
    )]
    public void FullMergeForward(string? existing, string[] operands, string expected)
    {
        LogIndexStorage.MergeOperator op = CreateOperator();
        CreateEnumerator(Serialize(existing), operands.Select(Serialize).ToArray(), out RocksDbMergeEnumerator enumerator);

        var key = GenerateKey(Address.Size, isBackward: false);
        Assert.That(
            Deserialize(op.FullMerge(key, enumerator)?.ToArray()),
            Is.EqualTo(expected.Split(',').Select(int.Parse).ToArray())
        );
    }

    [OneTimeTearDown]
    public static void OneTimeTearDown() => Handles.ForEach(h => h.Free());

    private static readonly LogIndexStorage.ICompressor Compressor = new LogIndexStorage.NoOpCompressor();

    private static readonly ConcurrentHashSet<GCHandle> Handles = [];

    private static LogIndexStorage.MergeOperator CreateOperator()
    {
        ILogIndexStorage storage = Substitute.For<ILogIndexStorage>();
        return new(storage, Compressor, 0);
    }

    private static void CreateEnumerator(byte[]? existingValue, byte[][] operands, out RocksDbMergeEnumerator enumerator)
    {
        var operandsPtrs = new IntPtr[operands.Length];
        var operandsLengths = operands.Select(x => (long)x.Length).ToArray();

        for (int i = 0; i < operands.Length; i++)
        {
            var handle = GCHandle.Alloc(operands[i], GCHandleType.Pinned);
            Handles.Add(handle);

            operandsPtrs[i] = handle.AddrOfPinnedObject();
            operandsLengths[i] = operands[i].Length;
        }

        enumerator = existingValue is null
            ? new(Span<byte>.Empty, false, operandsPtrs, operandsLengths)
            : new(existingValue, true, operandsPtrs, operandsLengths);
    }

    private static byte[] GenerateKey(int prefixSize, bool isBackward) => Random.Shared
        .NextBytes(prefixSize + LogIndexStorage.BlockNumSize)
        .Concat(isBackward ? LogIndexStorage.SpecialPostfix.BackwardMerge : LogIndexStorage.SpecialPostfix.ForwardMerge)
        .ToArray();

    private static byte[]? Serialize(string? input) =>
        input is null ? null : Bytes.Concat(input.Split(',').Select(s => s.Trim()).Select(s => s switch
        {
            _ when int.TryParse(s, out int blockNum) => blockNum.ToLittleEndianByteArray(),
            _ when TryParseMergeOp(s, out Span<byte> op) => op.ToArray(),
            _ => throw new FormatException($"Invalid operand: \"{input}\".")
        }).ToArray());

    private static bool TryParseMergeOp(string input, out Span<byte> bytes)
    {
        bytes = default;

        var parts = input.Split(":");
        if (parts.Length != 2) return false;

        if (!Enum.TryParse(parts[0], out LogIndexStorage.MergeOp op)) return false;
        if (!int.TryParse(parts[1], out int blockNum)) return false;

        bytes = LogIndexStorage.MergeOps.Create(op, blockNum).ToArray();
        return true;
    }

    private static int[]? Deserialize(byte[]? input) => input is null ? null : MemoryMarshal.Cast<byte, int>(input).ToArray();
}
