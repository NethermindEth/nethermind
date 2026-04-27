// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
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

        /// <summary>
        /// Returns the normalized input version, that produces the same <see cref="Run"/> output.
        /// </summary>
        /// <remarks>
        /// Used to minimize caching memory usage by grouping same-result inputs under the same key.
        /// Should be much faster than executing <see cref="Run"/> itself and not allocate.
        /// </remarks>
        ReadOnlyMemory<byte> NormalizeInput(ReadOnlyMemory<byte> inputData) => inputData;

        long BaseGasCost(IReleaseSpec releaseSpec);
        long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec);

        // N.B. returns a byte array so that inputData cannot be returned
        // this can lead to the wrong value being returned due to the cache modifying inputData
        Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec);
    }


    public interface IPrecompile<out TPrecompileTypeInstance> : IPrecompile
    {
        static abstract TPrecompileTypeInstance Instance { get; }
    }
}
