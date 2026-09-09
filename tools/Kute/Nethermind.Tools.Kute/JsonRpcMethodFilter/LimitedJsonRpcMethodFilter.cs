// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.JsonRpcMethodFilter;

public sealed class LimitedJsonRpcMethodFilter(IJsonRpcMethodFilter filter, int limit) : IJsonRpcMethodFilter
{
    private readonly IJsonRpcMethodFilter _filter = filter;

    private int _usagesLeft = limit;

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
