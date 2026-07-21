// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api.Steps;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;

namespace Nethermind.Init.Steps;

/// <summary>
/// One-shot, throwaway migration for flat-history indexes captured before the contiguous-watermark format. Such an
/// index has a per-block marker for every captured block (empty value, no watermark, no state root), which the new
/// read path treats as "no history". This step derives the contiguous-from-genesis watermark from those markers and
/// backfills each marker with the canonical block's state root, so a from-genesis archive keeps full coverage without
/// reprocessing.
/// </summary>
/// <remarks>
/// Delete this step (and its registration in <c>FlatHistoryModule</c>) once every operating node has migrated — nodes
/// that start on the new format never need it. It is idempotent and self-skips in O(1) once no captured marker sits
/// above the watermark.
/// </remarks>
[RunnerStepDependencies(typeof(InitializeBlockTree))]
public class MigrateFlatHistoryIndex(
    IColumnsDb<FlatHistoryColumns> history,
    IBlockTree blockTree,
    ILogManager logManager) : IStep
{
    // These MUST mirror Nethermind.State.Flat.History.HistoryAvailability; kept local so this throwaway step stays
    // fully self-contained (no coupling to the history assembly's internals).
    private const byte FormatVersion = 1;
    private const int BlockBytes = sizeof(ulong);
    private static ReadOnlySpan<byte> WatermarkKey => "history:watermark"u8;
    private static ReadOnlySpan<byte> FormatVersionKey => "history:format"u8;
    private const int BatchBlocks = 100_000;

    private readonly ILogger _logger = logManager.GetClassLogger<MigrateFlatHistoryIndex>();

    public Task Execute(CancellationToken cancellationToken)
    {
        IDb availableBlocks = history.GetColumnDb(FlatHistoryColumns.AvailableBlocks);

        bool hasWatermark = TryGetWatermark(availableBlocks, out ulong watermark);
        ulong from = hasWatermark ? watermark + 1 : 0;

        // Fast path: if no captured marker sits above the watermark, the index is already in the new format.
        if (!MarkerExists(availableBlocks, from)) return Task.CompletedTask;

        if (_logger.IsInfo) _logger.Info($"Migrating flat history index to the watermark format from block {from}...");

        ulong lastContiguous = hasWatermark ? watermark : 0;
        bool migratedAny = false;
        Span<byte> blockKey = stackalloc byte[BlockBytes];

        for (ulong block = from; !cancellationToken.IsCancellationRequested; block += BatchBlocks)
        {
            using IColumnsWriteBatch<FlatHistoryColumns> batch = history.StartWriteBatch();
            IWriteBatch markers = batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks);

            bool reachedEnd = false;
            for (ulong b = block; b < block + BatchBlocks; b++)
            {
                if (!MarkerExists(availableBlocks, b)) { reachedEnd = true; break; }

                // A marker without a canonical header (or state root) can't be bound to a root, so stop the
                // contiguous run there rather than claim coverage we can't validate against a block hash.
                if (blockTree.FindHeader(b)?.StateRoot is not { } stateRoot) { reachedEnd = true; break; }

                BinaryPrimitives.WriteUInt64BigEndian(blockKey, b);
                markers.PutSpan(blockKey, stateRoot.Bytes);
                lastContiguous = b;
                migratedAny = true;
            }

            if (reachedEnd) break;
        }

        if (migratedAny)
        {
            PublishWatermark(availableBlocks, lastContiguous);
            if (_logger.IsInfo) _logger.Info($"Flat history index migrated; contiguous history available up to block {lastContiguous}.");
        }

        return Task.CompletedTask;
    }

    private static bool TryGetWatermark(IDb availableBlocks, out ulong watermark)
    {
        byte[]? value = availableBlocks.Get(WatermarkKey);
        if (value is not { Length: BlockBytes })
        {
            watermark = 0;
            return false;
        }

        watermark = BinaryPrimitives.ReadUInt64BigEndian(value);
        return true;
    }

    private static bool MarkerExists(IDb availableBlocks, ulong block)
    {
        Span<byte> key = stackalloc byte[BlockBytes];
        BinaryPrimitives.WriteUInt64BigEndian(key, block);
        return availableBlocks.KeyExists(key);
    }

    private static void PublishWatermark(IDb availableBlocks, ulong watermark)
    {
        Span<byte> value = stackalloc byte[BlockBytes];
        BinaryPrimitives.WriteUInt64BigEndian(value, watermark);
        availableBlocks.PutSpan(WatermarkKey, value);
        availableBlocks.PutSpan(FormatVersionKey, [FormatVersion]);
    }
}
