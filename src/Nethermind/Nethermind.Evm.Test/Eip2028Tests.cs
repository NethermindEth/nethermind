// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip2028Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => MainnetSpecProvider.IstanbulBlockNumber;
        protected override ISpecProvider SpecProvider => new CustomSpecProvider(((ForkActivation)0, Istanbul.Instance));

        [TestCase(true, GasCostOf.TxDataNonZeroEip2028, Description = "After Istanbul non-zero cost is 16")]
        [TestCase(false, GasCostOf.TxDataNonZero, Description = "Before Istanbul non-zero cost is 68")]
        public void Non_zero_transaction_data_cost(bool isIstanbul, long expectedNonZeroCost)
        {
            IReleaseSpec spec = isIstanbul
                ? Istanbul.Instance
                : MainnetSpecProvider.Instance.GetSpec((ForkActivation)(MainnetSpecProvider.IstanbulBlockNumber - 1));
            Transaction transaction = new Transaction { Data = new byte[] { 1 }, To = Address.Zero };
            EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, spec);
            Assert.That(cost, Is.EqualTo(new EthereumIntrinsicGas(Standard: GasCostOf.Transaction + expectedNonZeroCost,
                FloorGas: 0)));
        }

        [Test]
        public void Zero_transaction_data_cost_should_be_4()
        {
            Transaction transaction = new Transaction { Data = new byte[] { 0 }, To = Address.Zero };
            EthereumIntrinsicGas cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
            Assert.That(cost, Is.EqualTo(new EthereumIntrinsicGas(Standard: GasCostOf.Transaction + GasCostOf.TxDataZero,
                FloorGas: 0)));
        }
    }
}
