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

using System.Numerics;
using System.Security.Cryptography;
using Nevermind.Core.Extensions;

namespace Nevermind.Evm.Precompiles
{
    public class Ripemd160PrecompiledContract : IPrecompiledContract
    {
        public static readonly IPrecompiledContract Instance = new Ripemd160PrecompiledContract();

        private static RIPEMD160 _ripemd;

        private Ripemd160PrecompiledContract()
        {
            _ripemd = RIPEMD160.Create();
            _ripemd.Initialize();
        }

        public BigInteger Address => 3;

        public long BaseGasCost()
        {
            return 600L;
        }

        public long DataGasCost(byte[] inputData)
        {
            return 120L * EvmMemory.Div32Ceiling(inputData.Length);
        }

        public byte[] Run(byte[] inputData)
        {
            return _ripemd.ComputeHash(inputData).PadLeft(32);
        }
    }
}