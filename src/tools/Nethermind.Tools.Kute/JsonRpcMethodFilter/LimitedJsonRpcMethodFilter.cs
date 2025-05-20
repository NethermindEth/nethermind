// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.JsonRpcMethodFilter;

class LimitedJsonRpcMethodFilter : IJsonRpcMethodFilter
{
    private readonly IJsonRpcMethodFilter _filter;

    private int _usagesLeft;

    public LimitedJsonRpcMethodFilter(IJsonRpcMethodFilter filter, int limit)
    {
        _filter = filter;
        _usagesLeft = limit;
    }

    public bool ShouldSubmit(string methodName)
    {
        if (_filter.ShouldSubmit(methodName))
        {
            if (_usagesLeft == 0)
            {
                return false;
            }

            _usagesLeft--;
            return true;
        }

        return false;
    }
}
