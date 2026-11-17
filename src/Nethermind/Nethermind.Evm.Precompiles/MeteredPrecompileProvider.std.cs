// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// Wraps every cacheable precompile of <paramref name="inner"/> in a <see cref="MeteredPrecompile"/>.
/// </summary>
/// <remarks>
/// The wrapped set is held here rather than in a static registry, so the reported series live and die with the container.
/// A precompile that does not support caching is left unwrapped: like <c>CachedPrecompile</c>, a decorator would hide the
/// concrete type and extra interfaces callers dispatch on, such as the VM's identity fast path.
/// </remarks>
public sealed class MeteredPrecompileProvider(IPrecompileProvider inner) : IPrecompileProvider
{
    private readonly FrozenDictionary<AddressAsKey, CodeInfo> _precompiles = Meter(inner);

    public FrozenDictionary<AddressAsKey, CodeInfo> GetPrecompiles() => _precompiles;

    /// <summary> Copies the call count of every precompile this provider wraps into the exported metrics. </summary>
    public void PublishMetrics()
    {
        foreach (KeyValuePair<AddressAsKey, CodeInfo> precompile in _precompiles)
        {
            if (precompile.Value.Precompile is MeteredPrecompile metered) metered.PublishMetrics();
        }
    }

    private static FrozenDictionary<AddressAsKey, CodeInfo> Meter(IPrecompileProvider inner) =>
        !ExecutionMetricsFlag.IsActive
            ? inner.GetPrecompiles()
            : inner.GetPrecompiles().ToFrozenDictionary(
                static precompile => precompile.Key,
                static precompile => precompile.Value.Precompile!.SupportsCaching
                    ? new CodeInfo(new MeteredPrecompile(precompile.Value.Precompile))
                    : precompile.Value);
}
