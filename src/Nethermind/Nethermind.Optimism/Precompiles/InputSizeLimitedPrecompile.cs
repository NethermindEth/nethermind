// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;

namespace Nethermind.Optimism.Precompiles;

/// <summary>
/// Decorates an <see cref="IPrecompile"/> with an OP-Stack input-size restriction, rejecting
/// oversized inputs before delegating the actual computation to the wrapped precompile.
/// </summary>
/// <remarks>
/// OP-Stack forks cap the input size of several expensive precompiles to bound worst-case
/// execution time. The limit depends on the active fork, so it is supplied as a function of the
/// <see cref="IReleaseSpec"/>; a <c>null</c> result means no OP-Stack specific limit applies for that spec.
/// </remarks>
public sealed class InputSizeLimitedPrecompile(IPrecompile inner, Func<IReleaseSpec, int?> maxInputSize) : PrecompileDecorator(inner)
{
    public override Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        int? limit = maxInputSize(releaseSpec);
        return limit is not null && inputData.Length > limit
            ? Errors.InvalidInputLength
            : base.Run(inputData, releaseSpec);
    }
}
