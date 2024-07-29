// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.JsonRpcMethodFilter;

class ComposedJsonRpcMethodFilter(IEnumerable<IJsonRpcMethodFilter> filters) : IJsonRpcMethodFilter
{
    private readonly IEnumerable<IJsonRpcMethodFilter> _filters = filters;

    private bool HasFilters => _filters?.Any() ?? false;

    public bool ShouldSubmit(string methodName) => !HasFilters || _filters.Any(f => f.ShouldSubmit(methodName));
}
