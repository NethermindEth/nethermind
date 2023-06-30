// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip1344Tests : VirtualMachineTestsBase
    {
        private void Test(ulong expectedChainId)
        {
            byte[] code = Prepare.EvmCode
                .Op(Instruction.CHAINID)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;
            TestAllTracerWithOutput result = Execute(code);
            long setCost = expectedChainId == 0 ? GasCostOf.SStoreNetMeteredEip2200 : GasCostOf.SSet;
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertGas(result, 21000 + GasCostOf.VeryLow + GasCostOf.Base + setCost);
            AssertStorage(0, ((UInt256)expectedChainId).ToBigEndian());
        }

        private class Custom0 : Eip1344Tests
        {
            protected override long BlockNumber => MainnetSpecProvider.IstanbulBlockNumber;
            protected override ISpecProvider SpecProvider => new CustomSpecProvider(((ForkActivation)0, Istanbul.Instance));

            [Test]
            public void given_custom_0_network_chain_id_opcode_puts_expected_value_onto_the_stack()
            {
                Test(SpecProvider.ChainId);
            }
        }

        private class Custom32000 : Eip1344Tests
        {
            protected override long BlockNumber => MainnetSpecProvider.IstanbulBlockNumber;
            protected override ISpecProvider SpecProvider => new CustomSpecProvider(((ForkActivation)0, Istanbul.Instance));

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
            protected override long BlockNumber => MainnetSpecProvider.IstanbulBlockNumber;
            protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;

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
    }
}
