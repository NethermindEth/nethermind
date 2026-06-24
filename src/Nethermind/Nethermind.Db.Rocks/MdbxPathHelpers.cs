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
        normalized = normalized.TrimEnd('/');
        return normalized.Equals("state", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/state", StringComparison.OrdinalIgnoreCase) ||
            IsIndexedStatePath(normalized);
    }

    private static bool IsIndexedStatePath(string normalized)
    {
        string index = string.Empty;
        if (normalized.StartsWith("state/", StringComparison.OrdinalIgnoreCase))
        {
            index = normalized["state/".Length..];
        }
        else
        {
            int stateIndex = normalized.LastIndexOf("/state/", StringComparison.OrdinalIgnoreCase);
            if (stateIndex >= 0)
            {
                index = normalized[(stateIndex + "/state/".Length)..];
            }
        }

        if (index.Length == 0)
        {
            return false;
        }

        for (int i = 0; i < index.Length; i++)
        {
            if (!char.IsDigit(index[i]))
            {
                return false;
            }
        }

        return true;
    }
}
