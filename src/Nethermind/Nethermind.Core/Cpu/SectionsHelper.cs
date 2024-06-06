// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Derived from https://github.com/dotnet/BenchmarkDotNet
// Licensed under the MIT License

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Nethermind.Core.Cpu;

internal static partial class SectionsHelper
{
    internal static readonly char[] separator = new[] { '\r', '\n' };

    public static Dictionary<string, string> ParseSection(string? content, char separator)
    {
        var values = new Dictionary<string, string>();
        var list = content?.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        if (list is not null)
            foreach (string line in list)
                if (line.Contains(separator))
                {
                    var lineParts = line.Split(separator);
                    if (lineParts.Length >= 2)
                        values[lineParts[0].Trim()] = lineParts[1].Trim();
                }
        return values;
    }

    public static List<Dictionary<string, string>> ParseSections(string? content, char separator)
    {
        // wmic doubles the carriage return character due to a bug.
        // Therefore, the * quantifier should be used to workaround it.
        return
            ParseRegex().Split(content ?? "")
                .Select(s => ParseSection(s, separator))
                .Where(s => s.Count > 0)
                .ToList();
    }

    [GeneratedRegex("(\r*\n){2,}")]
    private static partial Regex ParseRegex();
}
