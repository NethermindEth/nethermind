// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using Nethermind.Core;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// Wraps every precompile of <paramref name="inner"/> in a <see cref="MeteredPrecompile"/>.
/// </summary>
public sealed class MeteredPrecompileProvider(IPrecompileProvider inner) : IPrecompileProvider
{
    private readonly FrozenDictionary<AddressAsKey, CodeInfo> _precompiles = Meter(inner);

    public FrozenDictionary<AddressAsKey, CodeInfo> GetPrecompiles() => _precompiles;

    private static FrozenDictionary<AddressAsKey, CodeInfo> Meter(IPrecompileProvider inner) =>
        !ExecutionMetricsFlag.IsActive
            ? inner.GetPrecompiles()
            : inner.GetPrecompiles().ToFrozenDictionary(
                static precompile => precompile.Key,
                static precompile => new CodeInfo(new MeteredPrecompile(precompile.Value.Precompile!)));
}
