// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Globalization;
using Nethermind.Logging;

namespace Nethermind.State.Flat.History.Changesets;

internal static class BulkFillImportProgress
{
    public static double Fraction(ReadOnlySpan<byte> key) => key.IsEmpty ? 0 : BinaryPrimitives.ReadUInt32BigEndian(key) / 4294967296d;

    public static TimeSpan? Remaining(double initialFraction, double fraction, TimeSpan elapsed, bool complete)
    {
        if (complete) return TimeSpan.Zero;
        double advanced = fraction - initialFraction;
        if (advanced <= 0 || elapsed <= TimeSpan.Zero) return null;
        double seconds = elapsed.TotalSeconds * (1 - fraction) / advanced;
        return double.IsFinite(seconds) && seconds >= 0 && seconds < TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.FromSeconds(seconds) : null;
    }

    public static string Format(FlatHistoryColumns column, int stage, long rows, double initialFraction,
        double fraction, TimeSpan elapsed, bool complete, long sstBytes)
    {
        double displayed = complete ? 1 : Math.Min(fraction, 0.9999);
        double rate = elapsed.TotalSeconds > 0 ? rows / elapsed.TotalSeconds : 0;
        TimeSpan? remaining = Remaining(initialFraction, fraction, elapsed, complete);
        string eta = remaining is { } time ? time.ToString(@"d\.hh\:mm\:ss", CultureInfo.InvariantCulture) : "n/a";
        return FormattableString.Invariant($"Bulk transaction index importing {column} ({stage}/3): {Progress.GetMeter((float)displayed, 1)} {displayed:P2} keyspace | {rows:N0} rows this run | {rate:N0} rows/s | elapsed {elapsed:d\\.hh\\:mm\\:ss} | estimated column ETA {eta} (keyspace-based) | scratch SST/blob {sstBytes / (1024 * 1024):N0} MiB.");
    }
}
