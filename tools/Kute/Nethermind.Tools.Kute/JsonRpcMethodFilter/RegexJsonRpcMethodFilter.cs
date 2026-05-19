// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.RegularExpressions;

namespace Nethermind.Tools.Kute.JsonRpcMethodFilter;

public sealed class RegexJsonRpcMethodFilter : IJsonRpcMethodFilter
{
    private readonly Regex _pattern;

    public RegexJsonRpcMethodFilter(string pattern)
    {
        // The filter is constructed once and reused for every JSON-RPC method name; compile the
        // pattern up-front so the hot path skips the interpreter on every IsMatch call.
        _pattern = new Regex(pattern, RegexOptions.Compiled);
    }

    public bool ShouldSubmit(string methodName) => _pattern.IsMatch(methodName);
}
