// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.RegularExpressions;

namespace Nethermind.Tools.Kute.JsonRpcMethodFilter;

public sealed class RegexJsonRpcMethodFilter(string pattern) : IJsonRpcMethodFilter
{
    private readonly Regex _pattern = new(pattern, RegexOptions.Compiled);

    public bool ShouldSubmit(string methodName) => _pattern.IsMatch(methodName);
}
