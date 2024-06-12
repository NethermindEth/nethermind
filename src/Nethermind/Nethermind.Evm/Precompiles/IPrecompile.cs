// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles
{
    public interface IPrecompile
    {
        static virtual Address Address => Address.Zero;

        long BaseGasCost(IReleaseSpec releaseSpec);

        long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec);

        (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec);

        protected static (ReadOnlyMemory<byte>, bool) Failure { get; } = (Array.Empty<byte>(), false);
    }


    public interface IPrecompile<TPrecompileTypeInstance> : IPrecompile
    {
        static TPrecompileTypeInstance Instance { get; }
    }
}
