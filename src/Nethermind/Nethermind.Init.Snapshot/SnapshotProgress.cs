// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Init.Snapshot;

internal static class SnapshotProgress
{
    public static Func<ProgressLogger, string> FormatBytes(string prefix, long? totalBytes) =>
        totalBytes is null
            ? logger => $"{prefix} {HumanReadableSize(logger.CurrentValue)}"
            : logger => $"{prefix} {HumanReadableSize(logger.CurrentValue)} out of {HumanReadableSize((ulong)totalBytes.Value)}";

    private static string HumanReadableSize(ulong byteCount) =>
        byteCount switch
        {
            < MemorySizes.KiB => $"{byteCount:0.##}B",
            < MemorySizes.MiB => $"{(float)byteCount / MemorySizes.KiB:0.##}KB",
            < MemorySizes.GiB => $"{(float)byteCount / MemorySizes.MiB:0.##}MB",
            _ => $"{(float)byteCount / MemorySizes.GiB:0.##}GB",
        };
}
