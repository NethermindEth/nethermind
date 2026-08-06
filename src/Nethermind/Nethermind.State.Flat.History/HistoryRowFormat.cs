// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.State.Flat.History;

/// <summary>
/// The single owner of "which row format is this process using, and how are its rows shaped" — resolved once (via
/// <see cref="HistoryAvailability.ResolveFormatVersion"/>) and shared by every collaborator that needs to encode,
/// decode, or reason about <see cref="HistoryStore"/>/<see cref="HistoryStoreV3"/> row suffixes, instead of each
/// one independently calling <see cref="HistoryAvailability.ResolveFormatVersion"/> and re-deriving an
/// <c>_isV3</c> flag. This is what makes a decode format-correct by construction rather than by every call site
/// remembering which suffix direction applies.
/// </summary>
public sealed class HistoryRowFormat
{
    private const int BlockBytes = sizeof(ulong);

    public static HistoryRowFormat Resolve(HistoryAvailability availability, bool windowingConfigured) =>
        new(availability.ResolveFormatVersion(windowingConfigured));

    private HistoryRowFormat(byte formatVersion)
    {
        FormatVersion = formatVersion;
        IsV3 = formatVersion == HistoryAvailability.WindowedFormatVersion;
    }

    /// <summary>The resolved on-disk format byte — <see cref="HistoryAvailability.FormatVersion"/> or
    /// <see cref="HistoryAvailability.WindowedFormatVersion"/>.</summary>
    public byte FormatVersion { get; }

    /// <summary>Whether this process speaks v3 (pre-value, ascending suffix) rather than v2 (post-value,
    /// descending suffix) for the <c>AccountHistory</c>/<c>StorageHistory</c> columns.</summary>
    public bool IsV3 { get; }

    /// <summary>
    /// Whether the window pruner must retain exactly the newest row at or below the floor per key (v2 — its
    /// descending suffix means that single row is the answer every read in <c>[floor, next-change)</c> resolves
    /// to via a floor-seek) or may delete every row at or below the floor unconditionally (v3 — an ascending,
    /// pre-value row at or below the floor can never be the answer to a query at or above the floor, since a
    /// forward-seek only ever returns a row strictly above the query block; see <see cref="HistoryStoreV3"/>'s
    /// remarks).
    /// </summary>
    public bool RetainsNewestRowAtOrBelowFloor => !IsV3;

    /// <summary>
    /// Decodes an <c>AccountHistory</c>/<c>StorageHistory</c> row's block suffix back to a block number, per this
    /// format's suffix direction — the complement for v2's descending suffix, the raw value for v3's ascending
    /// one. <see cref="StorageClearStore"/> and <see cref="HistoryAvailability"/>'s own columns are unaffected:
    /// both use a plain ascending block suffix regardless of format, so only these two columns need this.
    /// </summary>
    public ulong DecodeSuffixBlock(ReadOnlySpan<byte> suffix)
    {
        ulong raw = BinaryPrimitives.ReadUInt64BigEndian(suffix);
        return IsV3 ? raw : ~raw;
    }
}
