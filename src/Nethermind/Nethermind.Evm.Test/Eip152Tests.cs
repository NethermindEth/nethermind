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

using Nethermind.Specs;
using Nethermind.Evm.Precompiles;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class Eip152Tests : VirtualMachineTestsBase
    {
        private const int InputLength = 213;
        protected override long BlockNumber => MainnetSpecProvider.IstanbulBlockNumber + _blockNumberAdjustment;

        private int _blockNumberAdjustment;

        [TearDown]
        public void TearDown()
        {
            _blockNumberAdjustment = 0;
        }
        
        [Test]
        public void before_istanbul()
        {
            _blockNumberAdjustment = -1;
            var precompileAddress = Blake2FPrecompile.Instance.Address;
            Assert.False(precompileAddress.IsPrecompile(Spec));
        }

        [Test]
        public void after_istanbul()
        {
            var code = Prepare.EvmCode
                .CallWithInput(Blake2FPrecompile.Instance.Address, 1000L, new byte[InputLength])
                .Done;
            var result = Execute(code);
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
        }
    }
}
