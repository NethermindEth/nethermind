// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// Base class for precompile decorators. Every member forwards to <paramref name="inner"/>; override
/// only what changes. Useful when a chain-specific layer (e.g. an OP-Stack input-size cap) needs to
/// wrap an existing precompile without re-implementing the unchanged surface.
/// </summary>
public abstract class PrecompileDecorator(IPrecompile inner) : IPrecompile
{
    protected IPrecompile Inner { get; } = inner;

    public virtual bool SupportsCaching => Inner.SupportsCaching;

    public virtual ReadOnlyMemory<byte> NormalizeInput(ReadOnlyMemory<byte> inputData) => Inner.NormalizeInput(inputData);

    public virtual long BaseGasCost(IReleaseSpec releaseSpec) => Inner.BaseGasCost(releaseSpec);

    public virtual long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => Inner.DataGasCost(inputData, releaseSpec);

    public virtual Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) => Inner.Run(inputData, releaseSpec);
}
