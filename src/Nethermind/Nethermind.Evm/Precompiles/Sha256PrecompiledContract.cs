/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles
{
    public class Sha256PrecompiledContract : IPrecompiledContract
    {
        public static readonly IPrecompiledContract Instance = new Sha256PrecompiledContract();

        private static SHA256 _sha256;

        private Sha256PrecompiledContract()
        {
            _sha256 = SHA256.Create();
            _sha256.Initialize();
        }

        public Address Address { get; } = Address.FromNumber(2);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 60L;
        }

        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec)
        {
            return 12L * EvmPooledMemory.Div32Ceiling((ulong)inputData.Length);
        }

        public (byte[],bool) Run(byte[] inputData)
        {
            Metrics.Sha256Precompile++;
            
            return (_sha256.ComputeHash(inputData), true);
        }
    }
}