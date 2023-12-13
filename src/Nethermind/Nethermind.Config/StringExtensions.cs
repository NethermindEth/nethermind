// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;

namespace Nethermind.Config
{
    public static class StringExtensions
    {
        public static string RemoveStart(this string thisString, char removeChar) =>
            thisString.StartsWith(removeChar) ? thisString[1..] : thisString;

        public static string RemoveEnd(this string thisString, char removeChar) =>
            thisString.EndsWith(removeChar) ? thisString[..^1] : thisString;

        public static bool Contains(this IEnumerable<string> collection, string value, StringComparison comparison) =>
            collection.Any(i => string.Equals(i, value, comparison));
    }
}
