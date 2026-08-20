// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Exceptions;
using Nethermind.Db;

namespace Nethermind.State.Flat.History;

/// <summary>The single owner of "which row format is this process using" - resolved once and shared, so no
/// collaborator re-derives its own flag or suffix direction.</summary>
public sealed class HistoryRowFormat
{
    /// <exception cref="InvalidConfigurationException">The resolved format is v3 on a layout other than
    /// <see cref="FlatLayout.Flat"/>.</exception>
    public static HistoryRowFormat Resolve(HistoryAvailability availability, IFlatDbConfig config)
    {
        HistoryRowFormat format = new(availability.ResolveFormatVersion(config.HistoryRetentionBlocks > 0));

        if (format.IsV3 && config.Layout != FlatLayout.Flat)
        {
            throw new InvalidConfigurationException(
                $"Flat history resolves to the windowed (v3) row format, which is only sound on FlatDb.Layout={nameof(FlatLayout.Flat)}; " +
                $"this node is configured for {config.Layout}. A v3 read that finds no captured change above the queried block falls " +
                $"through to the live flat Account column, and that column is keyed by the raw address under the preimage layouts and " +
                $"holds no accounts at all under {nameof(FlatLayout.FlatInTrie)} - so every account unchanged since the queried block " +
                $"would read as absent instead of failing. Set FlatDb.Layout={nameof(FlatLayout.Flat)}, or run unwindowed " +
                "(HistoryRetentionBlocks=0, no HistorySliceAddresses) on a database that has never been stamped windowed.", -1);
        }

        return format;
    }

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

    /// <summary>Whether the pruner must keep each key's newest row at or below the floor (v2: it answers every
    /// read in <c>[floor, next-change)</c>) or may delete everything at or below it (v3).</summary>
    public bool RetainsNewestRowAtOrBelowFloor => !IsV3;

    /// <summary>Decodes a history row's block suffix per this format's direction - complement for v2, raw for v3.
    /// Only the two versioned columns need this; clears and markers are always plain ascending.</summary>
    public ulong DecodeSuffixBlock(ReadOnlySpan<byte> suffix)
    {
        ulong raw = BinaryPrimitives.ReadUInt64BigEndian(suffix);
        return IsV3 ? raw : ~raw;
    }
}
