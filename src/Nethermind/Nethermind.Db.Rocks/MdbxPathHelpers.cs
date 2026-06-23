// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db.Rocks;

internal static class MdbxPathHelpers
{
    public static bool IsStateDbPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string normalized = path.Replace('\\', '/');
        return normalized.Contains("/state/", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/state", StringComparison.OrdinalIgnoreCase);
    }
}
