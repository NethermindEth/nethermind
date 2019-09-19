﻿/*
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
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles
{
    public class Sha256PrecompiledContract : IPrecompiledContract
    {
        private static ThreadLocal<SHA256> _sha256 = new ThreadLocal<SHA256>();
        
        public static readonly IPrecompiledContract Instance = new Sha256PrecompiledContract();

        private Sha256PrecompiledContract()
        {
            InitIfNeeded();
        }

        private static void InitIfNeeded()
        {
            if (!_sha256.IsValueCreated)
            {
                var sha = SHA256.Create();
                sha.Initialize();
                _sha256.Value = sha;
            }
        }

        public Address Address { get; } = Address.FromNumber(2);

        public long BaseGasCost(IReleaseSpec releaseSpec)
        {
            return 60L;
        }

        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec)
        {
            return 12L * EvmPooledMemory.Div32Ceiling((ulong) inputData.Length);
        }

        public (byte[], bool) Run(byte[] inputData)
        {
            Metrics.Sha256Precompile++;
            InitIfNeeded();
            return (_sha256.Value.ComputeHash(inputData), true);
        }
    }
}