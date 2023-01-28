// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
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
        private class AfterIstanbul : Eip2028Tests
        {
            protected override long BlockNumber => MainnetSpecProvider.IstanbulBlockNumber;
            protected override ISpecProvider SpecProvider => new CustomSpecProvider(((ForkActivation)0, Istanbul.Instance));

            [Test]
            public void non_zero_transaction_data_cost_should_be_16()
            {
                Transaction transaction = new Transaction { Data = new byte[] { 1 }, To = Address.Zero };
                long cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
                cost.Should().Be(GasCostOf.Transaction + GasCostOf.TxDataNonZeroEip2028);
            }

            [Test]
            public void zero_transaction_data_cost_should_be_4()
            {
                Transaction transaction = new Transaction { Data = new byte[] { 0 }, To = Address.Zero };
                long cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
                cost.Should().Be(GasCostOf.Transaction + GasCostOf.TxDataZero);
            }
        }

        private class BeforeIstanbul : Eip2028Tests
        {
            protected override long BlockNumber => MainnetSpecProvider.IstanbulBlockNumber - 1;
            protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;

            [Test]
            public void non_zero_transaction_data_cost_should_be_68()
            {
                Transaction transaction = new Transaction { Data = new byte[] { 1 }, To = Address.Zero };
                long cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
                cost.Should().Be(GasCostOf.Transaction + GasCostOf.TxDataNonZero);
            }

            [Test]
            public void zero_transaction_data_cost_should_be_4()
            {
                Transaction transaction = new Transaction { Data = new byte[] { 0 }, To = Address.Zero };
                long cost = IntrinsicGasCalculator.Calculate(transaction, Spec);
                cost.Should().Be(GasCostOf.Transaction + GasCostOf.TxDataZero);
            }
        }
    }
}
