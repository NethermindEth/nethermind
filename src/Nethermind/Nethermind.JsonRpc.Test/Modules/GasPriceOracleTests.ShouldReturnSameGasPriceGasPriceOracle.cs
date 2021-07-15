//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;
using NSubstitute;

namespace Nethermind.JsonRpc.Test.Modules
{
    partial class GasPriceOracleTests
    {
        private class ShouldReturnSameGasPriceGasPriceOracle : TestableGasPriceOracle
        {
            public ShouldReturnSameGasPriceGasPriceOracle(
                ISpecProvider? specProvider = null,
                UInt256? ignoreUnder = null, 
                int? blockLimit = null, 
                ITxInsertionManager? txInsertionManager = null,
                UInt256? lastGasPrice = null):
                base(
                    specProvider ?? Substitute.For<ISpecProvider>(),
                    ignoreUnder,
                    blockLimit,
                    txInsertionManager,
                    lastGasPrice)
            {
            }

            public override bool ShouldReturnSameGasPrice(Block? lastHead, Block? currentHead, UInt256? lastGasPrice)
            {
                return true;
            }
        }
    }
}
