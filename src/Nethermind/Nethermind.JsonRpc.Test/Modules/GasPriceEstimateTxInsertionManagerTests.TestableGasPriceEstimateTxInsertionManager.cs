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

using System.Collections.Generic;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Test.Modules
{
    partial class GasPriceEstimateTxInsertionManagerTests
    {
        private class TestableGasPriceEstimateTxInsertionManager : GasPriceEstimateTxInsertionManager
        {
            public readonly List<UInt256> _txGasPriceList;
            public TestableGasPriceEstimateTxInsertionManager(
                IGasPriceOracle gasPriceOracle,
                UInt256? ignoreUnder,
                ISpecProvider specProvider,
                List<UInt256> txGasPriceList = null) : 
                base(gasPriceOracle,
                    ignoreUnder,
                    specProvider)
            {
                _txGasPriceList = txGasPriceList ?? new List<UInt256>();
            }

            protected internal override List<UInt256> GetTxGasPriceList()
            {
                return _txGasPriceList;
            }
        }
    }
}
