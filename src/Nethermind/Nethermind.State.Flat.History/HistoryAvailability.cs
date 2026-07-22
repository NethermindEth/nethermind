// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;

namespace Nethermind.State.Flat.History;

/// <summary>
/// The <c>AvailableBlocks</c> column: per-block markers (<c>[block BE] -> 32-byte captured state root</c>) plus two
/// reserved keys — a contiguous-from-genesis watermark and a format version. An as-of read at block H is served only
/// when <c>H &lt;= watermark</c> (every block in <c>[0, H]</c> was captured, so a floor-seek cannot silently shadow a
/// gap) <em>and</em> the queried state root matches the captured root (so a non-canonical EIP-1898 hash below the
/// barrier is rejected rather than served the canonical value).
/// </summary>
internal sealed class HistoryAvailability
{
    // v2: no ChangeSets columns, descending block suffix — v1 data is unreadable under v2 seeks.
    internal const byte FormatVersion = 2;

    private const int BlockBytes = sizeof(ulong);
    private const int RootBytes = 32;

    // Reserved keys, deliberately not BlockBytes long so they can never collide with a per-block marker key.
    private static ReadOnlySpan<byte> WatermarkKey => "history:watermark"u8;
    private static ReadOnlySpan<byte> FormatVersionKey => "history:format"u8;

    private readonly IDb _availableBlocks;

    public HistoryAvailability(IDb availableBlocks)
    {
        ArgumentNullException.ThrowIfNull(availableBlocks);
        _availableBlocks = availableBlocks;
    }

    /// <summary>
    /// Refuses a history index written by an incompatible format. A fresh/empty index passes; the current version
    /// is stamped on the first <see cref="PublishWatermark"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The on-disk index uses a different format version.</exception>
    public void VerifyFormat()
    {
        byte[]? version = _availableBlocks.Get(FormatVersionKey);
        if (version is [FormatVersion]) return;

        bool hasLegacyData = version is not null;
        if (!hasLegacyData)
        {
            // Pre-versioning v1 stamped no format key, so any existing marker means old-layout data.
            foreach (KeyValuePair<byte[], byte[]?> _ in _availableBlocks.GetAll())
            {
                hasLegacyData = true;
                break;
            }
        }

        if (hasLegacyData)
        {
            throw new InvalidOperationException(
                $"The flat history database was written by an incompatible pre-release format " +
                $"(found version {(version is { Length: 1 } ? version[0].ToString() : "none")}, expected {FormatVersion}). " +
                "Delete the flatHistory database directory to re-capture history, or resync the node.");
        }
    }

    /// <summary>The highest block H such that every block in <c>[0, H]</c> has been captured; <c>false</c> when none has.</summary>
    public bool TryGetWatermark(out ulong watermark)
    {
        byte[]? value = _availableBlocks.Get(WatermarkKey);
        if (value is not { Length: BlockBytes })
        {
            watermark = 0;
            return false;
        }

        watermark = BinaryPrimitives.ReadUInt64BigEndian(value);
        return true;
    }

    /// <summary>Whether an as-of read at <paramref name="block"/> is backed by contiguous captured history.</summary>
    public bool IsCovered(ulong block) => TryGetWatermark(out ulong watermark) && block <= watermark;

    /// <summary>Whether <paramref name="block"/> is covered and its captured state root equals <paramref name="stateRoot"/>.</summary>
    public bool Matches(ulong block, in ValueHash256 stateRoot)
    {
        if (!IsCovered(block)) return false;

        Span<byte> key = stackalloc byte[BlockBytes];
        BinaryPrimitives.WriteUInt64BigEndian(key, block);
        byte[]? capturedRoot = _availableBlocks.Get(key);
        return capturedRoot is { Length: RootBytes } && stateRoot == new ValueHash256(capturedRoot);
    }

    /// <summary>Records the per-block marker (<c>block -> captured state root</c>) into <paramref name="batch"/>.</summary>
    /// <remarks>Also stamps the format version atomically with the marker: capture batches commit before any
    /// watermark publish, so a marker without a format key must never be an observable on-disk state — it would
    /// read as a pre-release layout on restart.</remarks>
    public static void MarkBlock(IWriteBatch batch, ulong block, in ValueHash256 stateRoot)
    {
        Span<byte> key = stackalloc byte[BlockBytes];
        BinaryPrimitives.WriteUInt64BigEndian(key, block);
        batch.PutSpan(key, stateRoot.Bytes);
        batch.PutSpan(FormatVersionKey, [FormatVersion]);
    }

    /// <summary>
    /// Publishes the contiguous watermark (and stamps the format version). Written outside the per-block capture
    /// batches so it advances only after the whole captured range is durable — a partial or failed capture leaves the
    /// watermark where it was, so reads above the gap fail closed.
    /// </summary>
    public void PublishWatermark(ulong watermark)
    {
        Span<byte> value = stackalloc byte[BlockBytes];
        BinaryPrimitives.WriteUInt64BigEndian(value, watermark);
        _availableBlocks.PutSpan(WatermarkKey, value);
        _availableBlocks.PutSpan(FormatVersionKey, [FormatVersion]);
    }
}
