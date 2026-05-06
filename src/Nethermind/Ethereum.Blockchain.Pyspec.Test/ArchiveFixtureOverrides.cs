// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Ethereum.Blockchain.Pyspec.Test;

internal static class ArchiveFixtureOverrides
{
    public static void Apply(string archiveVersion, string extractedArchiveRoot)
    {
        if (!string.Equals(archiveVersion, Constants.DEFAULT_ARCHIVE_VERSION, StringComparison.Ordinal))
        {
            return;
        }
    }
}
