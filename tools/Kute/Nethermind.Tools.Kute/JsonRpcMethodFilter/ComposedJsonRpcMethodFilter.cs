// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.JsonRpcMethodFilter;

public sealed class ComposedJsonRpcMethodFilter(List<IJsonRpcMethodFilter> filters) : IJsonRpcMethodFilter
{
    private readonly List<IJsonRpcMethodFilter> _filters = filters;
    private readonly bool _hasNoFilters = filters.Count == 0;

    public bool ShouldSubmit(string methodName) => _hasNoFilters || _filters.Any(f => f.ShouldSubmit(methodName));
}
