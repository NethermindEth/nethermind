// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Init.Snapshot;

internal static class SnapshotArchiveFormat
{
    public static bool IsZip(string extension) =>
        extension is ".zip";

    public static bool IsTarBased(string extension, string innerExtension) =>
        extension is ".tar" or ".zst" or ".zstd" or ".gz" or ".bz2" or ".xz" || innerExtension == ".tar";

    public static bool IsStreamable(string extension) =>
        extension is ".tar" or ".zst" or ".zstd" or ".gz";
}
