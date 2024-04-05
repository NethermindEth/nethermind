// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.IdentityModel.Tokens;

namespace Nethermind.Tools.Kute.JsonRpcMethodFilter;

class ComposedJsonRpcMethodFilter : IJsonRpcMethodFilter
{
    private readonly IEnumerable<IJsonRpcMethodFilter> _filters;

    public ComposedJsonRpcMethodFilter(IEnumerable<IJsonRpcMethodFilter> filters)
    {
        _filters = filters;
    }

    public bool ShouldSubmit(string methodName) =>
        _filters.IsNullOrEmpty() ||
        _filters.Any(f => f.ShouldSubmit(methodName));
}
