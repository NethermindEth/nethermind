// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.Db.LogIndex;

partial class LogIndexStorage
{
    // TODO: check if success=false + paranoid_checks=true is better than throwing an exception
    /// <summary>
    /// Merges log index incoming block number sequences into a single DB value.
    /// </summary>
    /// <remarks>
    /// Handles <see cref="IMergeableKeyValueStore.Merge"/> calls from <see cref="LogIndexStorage"/>.
    /// Operator works <b>only</b> with uncompressed data under "transient" keys. <br/>
    /// Each log index column family has its own merge operator instance,
    /// <paramref name="topicIndex"/> parameter is used to find corresponding DB. <br/>
    ///
    /// <para>
    /// Supports 2 different use cases depending on the incoming operand:
    /// <list type="number">
    /// <item>
    /// In case if operand is a sequence of <see cref="Int32"/> block numbers -
    /// directly concatenates with the existing DB value, validating order remains correct (see <see cref="AddEnsureSorted"/>). <br/>
    /// If value size grows to or over <see cref="ILogIndexConfig.CompressionDistance"/> block numbers -
    /// queues it for compression (via <see cref="ICompressor.TryEnqueue"/>).
    /// </item>
    /// <item>
    /// If operand from <see cref="MergeOps"/> - performs specified operation on the previously obtained sequence.
    /// </item>
    /// </list>
    /// Check MergeOperator tests for the expected behavior.
    /// </para>
    /// </remarks>
    public class MergeOperator(ILogIndexStorage storage, ICompressor compressor, int? topicIndex) : IMergeOperator
    {
        private LogIndexUpdateStats _stats = new(storage);
        public LogIndexUpdateStats Stats => _stats;
        public LogIndexUpdateStats GetAndResetStats() => Interlocked.Exchange(ref _stats, new(storage));

        public string Name => $"{nameof(LogIndexStorage)}.{nameof(MergeOperator)}";

        public ArrayPoolList<byte>? FullMerge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator) =>
            Merge(key, enumerator, isPartial: false);

        public ArrayPoolList<byte>? PartialMerge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator) =>
            Merge(key, enumerator, isPartial: true);

        private static bool IsBlockNewer(int next, int? last, bool isBackwardSync) =>
            LogIndexStorage.IsBlockNewer(next, last, last, isBackwardSync);

        // Validate we are merging non-intersecting segments - to prevent data corruption
        private static void AddEnsureSorted(ReadOnlySpan<byte> key, ArrayPoolList<byte> result, ReadOnlySpan<byte> value, bool isBackward)
        {
            if (value.Length == 0)
                return;

            var nextBlock = ReadBlockNumber(value);
            var lastBlock = result.Count > 0 ? ReadLastBlockNumber(result.AsSpan()) : (int?)null;

            if (!IsBlockNewer(next: nextBlock, last: lastBlock, isBackward))
                throw new LogIndexStateException($"Invalid order during merge: {lastBlock} -> {nextBlock} (backward: {isBackward}).", key);

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
                var truncateAggregate = Aggregate(MergeOp.Truncate, enumerator, isBackwards);

                var iReorg = 0;
                for (var i = 0; i < enumerator.TotalCount; i++)
                {
                    Span<byte> operand = enumerator.Get(i);

                    if (MergeOps.IsAny(operand))
                        continue;

                    // For reorg - order matters, so we need to always traverse from the current position
                    iReorg = Math.Max(iReorg, i + 1);
                    if (FindNext(MergeOp.Reorg, enumerator, ref iReorg) is { } reorgBlock)
                        operand = MergeOps.ApplyTo(operand, MergeOp.Reorg, reorgBlock, isBackwards);

                    if (truncateAggregate is { } truncateBlock)
                        operand = MergeOps.ApplyTo(operand, MergeOp.Truncate, truncateBlock, isBackwards);

                    AddEnsureSorted(key, result, operand, isBackwards);
                }

                if (result.Count % BlockNumberSize != 0)
                    throw new LogIndexStateException($"Invalid data length post-merge: {result.Count}.", key);

                compressor.TryEnqueue(topicIndex, key, result.AsSpan());

                success = true;
                return result;
            }
            catch (Exception exception)
            {
                (storage as LogIndexStorage)?.OnBackgroundError<MergeOperator>(exception);
                return null;
            }
            finally
            {
                if (!success) result?.Dispose();

                _stats.BackgroundMerging.Include(Stopwatch.GetElapsedTime(timestamp));
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
