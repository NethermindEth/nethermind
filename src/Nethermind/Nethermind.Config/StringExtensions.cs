// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Config
{
    public static class StringExtensions
    {
        public static string RemoveStart(this string thisString, char removeChar) =>
            thisString.StartsWith(removeChar) ? thisString.Substring(1, thisString.Length - 1) : thisString;

        public static string RemoveEnd(this string thisString, char removeChar) =>
            thisString.EndsWith(removeChar) ? thisString.Substring(0, thisString.Length - 1) : thisString;
    }
}
