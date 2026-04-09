// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core;

public static class LintTest
{
    // IDE0005: unused using (System.Collections.Generic above)

    // IDE0100: unnecessary equality operator
    public static bool IsZero(int x) => x == 0 == true;

    // IDE0008: use explicit type instead of var
    public static int GetLength(string s)
    {
        var result = s.Length;
        return result;
    }

    // CA1825: use Array.Empty<T>()
    public static int[] GetEmpty() => new int[0];

    // CA1829: use Length instead of Count()
    public static int CountItems(int[] items) => System.Linq.Enumerable.Count(items);

    // CA1834: use StringBuilder.Append(char) for single-char strings
    public static string Build()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("/");
        return sb.ToString();
    }

    // CA1507: use nameof
    public static string GetName() => "GetName";

    // CA1510: use ArgumentNullException.ThrowIfNull
    public static void Validate(object? obj)
    {
        if (obj is null) throw new System.ArgumentNullException("obj");
    }
}
