// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class MergeOperatorTests
{
    private static readonly LogIndexStorage.ICompressor Compressor = new LogIndexStorage.NoOpCompressor();

    private static readonly ConcurrentHashSet<GCHandle> Handles = [];

    [OneTimeTearDown]
    public static void OneTimeTearDown() => Handles.ForEach(h => h.Free());

    private static LogIndexStorage.MergeOperator CreateOperator()
    {
        ILogIndexStorage storage = Substitute.For<ILogIndexStorage>();
        return new(storage, Compressor, 0);
    }

    private static LogIndexStorage.MergeOperator CreateOperator2()
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

    private static void CreateEnumerator(LogPosition[]? existingValue, object[] operands, out RocksDbMergeEnumerator enumerator)
    {
        var existingBytes = existingValue is null
            ? null
            : MemoryMarshal.Cast<LogPosition, byte>(existingValue).ToArray();

        var operandsBytes = operands
            .Select(x => x switch
            {
                byte[] bytes => bytes,
                LogPosition[] positions => MemoryMarshal.Cast<LogPosition, byte>(positions).ToArray(),
                _ => throw new NotSupportedException()
            })
            .ToArray();

        CreateEnumerator(existingBytes, operandsBytes, out enumerator);
    }

    private static byte[] GenerateKey(int prefixSize, bool isBackward) => Random.Shared
        .NextBytes(prefixSize + LogIndexStorage.ValueSize)
        .Concat(isBackward ? LogIndexStorage.SpecialPostfix.BackwardMerge : LogIndexStorage.SpecialPostfix.ForwardMerge)
        .ToArray();

    private static IEnumerable<TestCaseData<LogPosition[]?, object[]>> MergeTestCases
    {
        get
        {
            yield return new(
                null,
                new LogPosition[] { new(1, 1), new(1, 2), new(2, 1), new(2, 2), new(2, 3) }
                    .Cast<object>().ToArray()
            )
            {
                ExpectedResult = new LogPosition[] { new(1, 1), new(1, 2), new(2, 1), new(2, 2), new(2, 3) }
            };

            yield return new(
                null,
                new LogPosition[] { new(1, 1), new(1, 2), new(2, 1), new(2, 2), new(2, 3) }
                    .Cast<object>().ToArray()
            )
            {
                ExpectedResult = new LogPosition[] { new(1, 1), new(1, 2), new(2, 1), new(2, 2), new(2, 3) }
            };
        }
    }

    private static byte[]? Serialize(string? input) => input is null ? null : Bytes.Concat(input.Split(',').Select(s => s.Trim()).Select(s => s switch
    {
        _ when LogPosition.TryParse(s, out LogPosition pos) => pos.ToArray(),
        _ when LogIndexStorage.MergeOps.TryParse(s, out Span<byte> op) => op.ToArray(),
        _ => throw new FormatException($"Invalid operand: \"{input}\".")
    }).ToArray());

    private static LogPosition[]? Deserialize(byte[]? input) => input is null ? null : MemoryMarshal.Cast<byte, LogPosition>(input).ToArray();

    [TestCase(
        null,
        new[] { "1:0, 1:1", "2:0", "3:1, 4:1" },
        "1:0, 1:1, 2:0, 3:1, 4:1"
    )]
    [TestCase(
        "1:0, 1:1, 2:0",
        new[] { "2:2, 3:1", "3:3, 4:0" },
        "1:0, 1:1, 2:0, 2:2, 3:1, 3:3, 4:0"
    )]
    [TestCase(
        "1:0, 1:1, 2:0",
        new[] { "2:2, 3:1", "Reorg:3:0", "3:3, 4:0" },
        "1:0, 1:1, 2:0, 2:2, 3:3, 4:0"
    )]
    [TestCase(
        "1:0, 1:1, 2:0",
        new[] { "2:2, 3:1", "Truncate:2:2", "3:3, 4:0" },
        "3:1, 3:3, 4:0"
    )]
    public void FullMergeForward(string? existing, string[] operands, string expected)
    {
        LogIndexStorage.MergeOperator op = CreateOperator2();
        CreateEnumerator(Serialize(existing), operands.Select(Serialize).ToArray(), out RocksDbMergeEnumerator enumerator);

        var key = GenerateKey(Address.Size, isBackward: false);
        Assert.That(
            Deserialize(op.FullMerge(key, enumerator)?.ToArray()),
            Is.EqualTo(expected.Split(',').Select(LogPosition.Parse).ToArray())
        );
    }
}
