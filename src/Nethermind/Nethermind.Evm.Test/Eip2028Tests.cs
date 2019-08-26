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

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip2028Tests : VirtualMachineTestsBase
    {
        private IntrinsicGasCalculator _gasCalculator;

        public override void Setup()
        {
            base.Setup();
            _gasCalculator = new IntrinsicGasCalculator();
        }

        private class AfterIstanbul : Eip2028Tests
        {
            protected override long BlockNumber => MainNetSpecProvider.IstanbulBlockNumber;
            protected override ISpecProvider SpecProvider => new CustomSpecProvider(32000, (0, Istanbul.Instance));

            [Test]
            public void non_zero_transaction_data_cost_should_be_16()
            {
                var transaction = new Transaction {Data = new byte[] {1}};
                var cost = _gasCalculator.Calculate(transaction, Spec);
                cost.Should().Be(GasCostOf.Transaction + GasCostOf.TxDataNonZeroEip2028);
            }

            [Test]
            public void zero_transaction_data_cost_should_be_4()
            {
                var transaction = new Transaction {Data = new byte[] {0}};
                var cost = _gasCalculator.Calculate(transaction, Spec);
                cost.Should().Be(GasCostOf.Transaction + GasCostOf.TxDataZero);
            }
        }

        private class BeforeIstanbul : Eip2028Tests
        {
            protected override long BlockNumber => MainNetSpecProvider.IstanbulBlockNumber - 1;
            protected override ISpecProvider SpecProvider => MainNetSpecProvider.Instance;

            [Test]
            public void non_zero_transaction_data_cost_should_be_68()
            {
                var transaction = new Transaction {Data = new byte[] {1}};
                var cost = _gasCalculator.Calculate(transaction, Spec);
                cost.Should().Be(GasCostOf.Transaction + GasCostOf.TxDataNonZero);
            }

            [Test]
            public void zero_transaction_data_cost_should_be_4()
            {
                var transaction = new Transaction {Data = new byte[] {0}};
                var cost = _gasCalculator.Calculate(transaction, Spec);
                cost.Should().Be(GasCostOf.Transaction + GasCostOf.TxDataZero);
            }
        }
    }
}