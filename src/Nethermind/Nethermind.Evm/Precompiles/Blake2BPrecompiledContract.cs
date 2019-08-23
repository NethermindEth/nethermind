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

using Nethermind.Core;
using Nethermind.Core.Specs;
using SauceControl.Blake2Fast;

namespace Nethermind.Evm.Precompiles
{
    public class Blake2BPrecompiledContract : IPrecompiledContract
    {
        public static readonly IPrecompiledContract Instance = new Blake2BPrecompiledContract();

        public Address Address { get; } = Address.FromNumber(9);

        public long BaseGasCost(IReleaseSpec releaseSpec) => 0;

        public long DataGasCost(byte[] inputData, IReleaseSpec releaseSpec) => 12;

        public (byte[], bool) Run(byte[] inputData)
        {
            Metrics.Blake2BPrecompile++;
            var context = default(Blake2bContext);
            //TODO: compression function
//            context.Init(digestLength, key);
//            context.compress();
            var result = context.Finish();

            return (result, true);
        }
    }
}