// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Exceptions;
using Nethermind.Db;

namespace Nethermind.State.Flat.History;

/// <summary>The single owner of the process's row format, resolved once and shared.</summary>
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

    public byte FormatVersion { get; }

    /// <summary>v3 is pre-value with an ascending suffix; v2 is post-value with a descending one.</summary>
    public bool IsV3 { get; }


    /// <summary>Complement for v2, raw for v3. Clears and markers are always plain ascending.</summary>
    public ulong DecodeSuffixBlock(ReadOnlySpan<byte> suffix)
    {
        ulong raw = BinaryPrimitives.ReadUInt64BigEndian(suffix);
        return IsV3 ? raw : ~raw;
    }
}
