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
        static virtual string Name => string.Empty;
        bool SupportsCaching => true;
        long BaseGasCost(IReleaseSpec releaseSpec);
        long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec);

        // N.B. returns a byte array so that inputData cannot be returned
        // this can lead to the wrong value being returned due to the cache modifying inputData
        Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec);
    }


    public interface IPrecompile<TPrecompileTypeInstance> : IPrecompile
    {
        static TPrecompileTypeInstance Instance { get; }
    }
}
