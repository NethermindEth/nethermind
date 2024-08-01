// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.JsonRpcMethodFilter;

public class PatternJsonRpcMethodFilter : IJsonRpcMethodFilter
{
    private const char PatternSeparator = '=';

    private readonly IJsonRpcMethodFilter _filter;
    public PatternJsonRpcMethodFilter(string pattern)
    {
        var splitted = pattern.Split(PatternSeparator);

        var regex = new RegexJsonRpcMethodFilter(splitted[0]);
        _filter = splitted.Length switch
        {
            1 => regex,
            2 => new LimitedJsonRpcMethodFilter(regex, int.Parse(splitted[1])),
            _ => throw new ArgumentException($"Unexpected pattern: {pattern}"),
        };
    }

    public bool ShouldSubmit(string methodName) => _filter.ShouldSubmit(methodName);
}
