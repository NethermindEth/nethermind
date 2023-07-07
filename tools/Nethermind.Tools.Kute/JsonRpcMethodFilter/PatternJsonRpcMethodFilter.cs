// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.RegularExpressions;

namespace Nethermind.Tools.Kute.JsonRpcMethodFilter;

class PatternJsonRpcMethodFilter : IJsonRpcMethodFilter
{
    private readonly Regex _pattern;

    public PatternJsonRpcMethodFilter(string pattern)
    {
        _pattern = new Regex(pattern);
    }

    public bool ShouldSubmit(string methodName) => _pattern.IsMatch(methodName);
}
