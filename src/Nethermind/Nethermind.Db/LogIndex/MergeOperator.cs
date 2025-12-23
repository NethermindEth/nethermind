// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.Db.LogIndex;

partial class LogIndexStorage<TPosition>
{
    public class MergeOperator(ILogIndexStorage<TPosition> storage, ICompressor compressor, int? topicIndex) : IMergeOperator
    {
        private LogIndexUpdateStats _stats = new(storage);
        public LogIndexUpdateStats Stats => _stats;
        public LogIndexUpdateStats GetAndResetStats() => Interlocked.Exchange(ref _stats, new(storage));

        public string Name => $"{nameof(LogIndexStorage<>)}.{nameof(MergeOperator)}";

        public ArrayPoolList<byte>? FullMerge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator) =>
            Merge(key, enumerator, isPartial: false);

        public ArrayPoolList<byte>? PartialMerge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator) =>
            Merge(key, enumerator, isPartial: true);

        private bool IsPositionNewer(TPosition next, TPosition? last, bool isBackwardSync) =>
            LogIndexStorage<TPosition>.IsPositionNewer(next, last, last, isBackwardSync);

        // Validate we are merging non-intersecting segments - to prevent data corruption
        private void AddEnsureSorted(ReadOnlySpan<byte> key, ArrayPoolList<byte> result, ReadOnlySpan<byte> value, bool isBackwards)
        {
            if (value.Length == 0)
                return;

            TPosition nextPos = TPosition.ReadFirstFrom(value);
            TPosition? lastPos = result.Count > 0 ? TPosition.ReadLastFrom(result.AsSpan()) : null;

            if (!IsPositionNewer(next: nextPos, last: lastPos, isBackwards))
            {
                throw new LogIndexStateException(
                    $"Invalid order during merge: {lastPos} -> {nextPos} (backwards: {isBackwards}).",
                    key
                );
            }

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
                TPosition? truncateAggregate = Aggregate(MergeOp.Truncate, enumerator, isBackwards);

                var iReorg = 0;
                for (var i = 0; i < enumerator.TotalCount; i++)
                {
                    Span<byte> operand = enumerator.Get(i);

                    if (MergeOps.IsAny(operand))
                        continue;

                    // For reorg - order matters, so we need to always traverse from the current position
                    iReorg = Math.Max(iReorg, i + 1);
                    if (FindNext(MergeOp.Reorg, enumerator, ref iReorg) is { } reorgVal)
                        operand = MergeOps.ApplyTo(operand, MergeOp.Reorg, reorgVal, isBackwards);

                    if (truncateAggregate is { } truncateVal)
                        operand = MergeOps.ApplyTo(operand, MergeOp.Truncate, truncateVal, isBackwards);

                    AddEnsureSorted(key, result, operand, isBackwards);
                }

                if (result.Count % ValueSize != 0)
                    throw new LogIndexStateException($"Invalid data length post-merge: {result.Count}.", key);

                compressor.TryEnqueue(topicIndex, key, result.AsSpan());

                success = true;
                return result;
            }
            catch (Exception exception) when (storage is LogIndexStorage<TPosition> logIndexStorage)
            {
                logIndexStorage.OnBackgroundError<MergeOperator>(exception);
                return null;
            }
            finally
            {
                if (!success) result?.Dispose();

                _stats.InMemoryMerging.Include(Stopwatch.GetElapsedTime(timestamp));
            }
        }

        private TPosition? FindNext(MergeOp op, RocksDbMergeEnumerator enumerator, ref int i)
        {
            while (i < enumerator.TotalCount && !MergeOps.Is(op, enumerator.Get(i)))
                i++;

            if (i < enumerator.TotalCount && MergeOps.Is(op, enumerator.Get(i), out TPosition position))
                return position;

            return null;
        }

        private static TPosition? Aggregate(MergeOp op, RocksDbMergeEnumerator enumerator, bool isBackwardSync)
        {
            TPosition? result = null;
            for (var i = 0; i < enumerator.OperandsCount; i++)
            {
                if (!MergeOps.Is(op, enumerator.GetOperand(i), out TPosition next))
                    continue;

                if (result is null || (isBackwardSync && next < result) || (!isBackwardSync && next > result))
                    result = next;
            }

            return result;
        }
    }
}
