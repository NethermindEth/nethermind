/*
 * Copyright (c) 2018 Demerzel Solutions LimitedZ
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

using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip1344Tests : VirtualMachineTestsBase
    {
        private void Test(int chainId)
        {
            var code = Prepare.EvmCode
                .Op(Instruction.CHAINID)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;
            var result = Execute(code);
            var setCost = chainId == 0 ? GasCostOf.SStoreNetMeteredEip2200 : GasCostOf.SSet;
            Assert.AreEqual(StatusCode.Success, result.StatusCode);
            AssertGas(result, 21000 + GasCostOf.VeryLow + GasCostOf.Base + setCost);
            AssertStorage(0, chainId.ToBigEndianByteArray());
        }
        
        private class Custom0 : Eip1344Tests
        {
            protected override long BlockNumber => MainNetSpecProvider.IstanbulBlockNumber;
            protected override ISpecProvider SpecProvider => new CustomSpecProvider(0, (0, Istanbul.Instance));

            [Test]
            public void given_custom_0_network_chain_id_opcode_puts_expected_value_onto_the_stack()
            {
                Test(SpecProvider.ChainId);
            }
        }
        
        private class Custom32000 : Eip1344Tests
        {
            protected override long BlockNumber => MainNetSpecProvider.IstanbulBlockNumber;
            protected override ISpecProvider SpecProvider => new CustomSpecProvider(32000, (0, Istanbul.Instance));

            [Test]
            public void given_custom_custom_32000_network_chain_id_opcode_puts_expected_value_onto_the_stack()
            {
                Test(SpecProvider.ChainId);
            }
        }
        
        private class Goerli : Eip1344Tests
        {
            protected override long BlockNumber => GoerliSpecProvider.IstanbulBlockNumber;
            protected override ISpecProvider SpecProvider => GoerliSpecProvider.Instance;

            [Test]
            public void given_goerli_network_chain_id_opcode_puts_expected_value_onto_the_stack()
            {
                Test(SpecProvider.ChainId);
            }
        }
        
        private class Mainnet : Eip1344Tests
        {
            protected override long BlockNumber => MainNetSpecProvider.IstanbulBlockNumber;
            protected override ISpecProvider SpecProvider => MainNetSpecProvider.Instance;

            [Test]
            public void given_mainnet_network_chain_id_opcode_puts_expected_value_onto_the_stack()
            {
                Test(SpecProvider.ChainId);
            }
        }

        private class Rinkeby : Eip1344Tests
        {
            protected override long BlockNumber => RinkebySpecProvider.IstanbulBlockNumber;
            protected override ISpecProvider SpecProvider => RinkebySpecProvider.Instance;

            [Test]
            public void given_rinkeby_network_chain_id_opcode_puts_expected_value_onto_the_stack()
            {
                Test(SpecProvider.ChainId);
            }
        }
        
        private class Ropsten : Eip1344Tests
        {
            protected override long BlockNumber => RopstenSpecProvider.IstanbulBlockNumber;
            protected override ISpecProvider SpecProvider => RopstenSpecProvider.Instance;

            [Test]
            public void given_ropsten_network_chain_id_opcode_puts_expected_value_onto_the_stack()
            {
                Test(SpecProvider.ChainId);
            }
        }
    }
}