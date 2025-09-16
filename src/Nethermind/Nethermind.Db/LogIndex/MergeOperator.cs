// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.Db;

partial class LogIndexStorage
{
    // TODO: check if success=false + paranoid_checks=true is better than throwing exception
    // TODO: tests for MergeOperator specifically?
    private class MergeOperator(ILogIndexStorage logIndexStorage, ICompressor compressor, int? topicIndex) : IMergeOperator
    {
        private LogIndexUpdateStats _stats = new(logIndexStorage);
        public LogIndexUpdateStats GetAndResetStats() => Interlocked.Exchange(ref _stats, new(logIndexStorage));

        public string Name => $"{nameof(LogIndexStorage)}.{nameof(MergeOperator)}";

        public ArrayPoolList<byte>? FullMerge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator) =>
            Merge(key, enumerator, isPartial: false);

        public ArrayPoolList<byte>? PartialMerge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator) =>
            Merge(key, enumerator, isPartial: true);

        private static bool IsBlockNewer(int next, int? last, bool isBackwardSync) =>
            LogIndexStorage.IsBlockNewer(next, last, last, isBackwardSync);

        // Validate we are merging non-intersecting segments - to prevent data corruption
        // TODO: remove as it's just a time-consuming validation?
        private static void AddEnsureSorted(ArrayPoolList<byte> result, ReadOnlySpan<byte> value, bool isBackwards)
        {
            if (value.Length == 0)
                return;

            var nextBlock = GetValBlockNum(value);
            var lastBlock = result.Count > 0 ? GetValLastBlockNum(result.AsSpan()) : (int?)null;

            if (!IsBlockNewer(next: nextBlock, last: lastBlock, isBackwards))
                throw ValidationException($"Invalid order during merge: {lastBlock} -> {nextBlock} (backwards: {isBackwards}).");

            result.AddRange(value);
        }

        // TODO: avoid array copying in case of a single value?
        private ArrayPoolList<byte>? Merge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator, bool isPartial)
        {
            var success = false;
            ArrayPoolList<byte>? result = null;
            var timestamp = Stopwatch.GetTimestamp();

            try
            {
                // Fast return in case of a single operand
                if (!enumerator.HasExistingValue && enumerator.OperandsCount == 1 && !MergeOps.IsAny(enumerator.GetOperand(0)))
                    return new(enumerator.GetOperand(0));

                bool isBackwards = UseBackwardSyncFor(key);

                // Calculate total length
                var resultLength = enumerator.GetExistingValue().Length;
                for (var i = 0; i < enumerator.OperandsCount; i++)
                {
                    ReadOnlySpan<byte> operand = enumerator.GetOperand(i);

                    if (MergeOps.IsAny(operand))
                    {
                        if (isPartial)
                            return null; // Notify RocksDB that we can't partially merge custom ops

                        continue;
                    }

                    resultLength += operand.Length;
                }

                result = new(resultLength);

                // For truncate - just use max/min for all operands
                var truncateAggregate = Aggregate(MergeOp.TruncateOp, enumerator, isBackwards);

                var iReorg = 0;
                for (var i = 0; i < enumerator.TotalCount; i++)
                {
                    Span<byte> operand = enumerator.Get(i);

                    if (MergeOps.IsAny(operand))
                        continue;

                    // For reorg - order matters, so we need to always traverse from the current position
                    iReorg = Math.Max(iReorg, i + 1);
                    if (FindNext(MergeOp.ReorgOp, enumerator, ref iReorg) is { } reorgBlock)
                        operand = MergeOps.ApplyTo(operand, MergeOp.ReorgOp, reorgBlock, isBackwards);

                    if (truncateAggregate is { } truncateBlock)
                        operand = MergeOps.ApplyTo(operand, MergeOp.TruncateOp, truncateBlock, isBackwards);

                    AddEnsureSorted(result, operand, isBackwards);
                }

                if (result.Count % BlockNumSize != 0)
                    throw ValidationException("Invalid data length post-merge.");

                compressor.TryEnqueue(topicIndex, key, result.AsSpan());

                success = true;
                return result;
            }
            finally
            {
                if (!success) result?.Dispose();

                _stats.InMemoryMerging.Include(Stopwatch.GetElapsedTime(timestamp));
            }
        }

        private static int? FindNext(MergeOp op, RocksDbMergeEnumerator enumerator, ref int i)
        {
            while (i < enumerator.TotalCount && !MergeOps.Is(op, enumerator.Get(i)))
                i++;

            if (i < enumerator.TotalCount && MergeOps.Is(op, enumerator.Get(i), out int block))
                return block;

            return null;
        }

        private static int? Aggregate(MergeOp op, RocksDbMergeEnumerator enumerator, bool isBackwardSync)
        {
            int? result = null;
            for (var i = 0; i < enumerator.OperandsCount; i++)
            {
                if (!MergeOps.Is(op, enumerator.GetOperand(i), out var next))
                    continue;

                if (result is null || (isBackwardSync && next < result) || (!isBackwardSync && next > result))
                    result = next;
            }

            return result;
        }
    }
}
