// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;

namespace Nethermind.Init;

public static class VersionToMetrics
{
    private static readonly char[] anyOf = new[] { '-', '+' };

    public static int ConvertToNumber(string version)
    {
        try
        {
            var index = version.IndexOfAny(anyOf);

            if (index != -1)
                version = version[..index];

            var versions = version
                .Split('.')
                .Select(v => int.Parse(v))
                .ToArray();

            return versions.Length == 3
                ? versions[0] * 100_000 + versions[1] * 1_000 + versions[2]
                : throw new ArgumentException("Invalid version format");
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
