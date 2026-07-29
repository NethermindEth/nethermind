// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip2200Tests : VirtualMachineTestsBase
    {
        protected override ulong BlockNumber => MainnetSpecProvider.IstanbulBlockNumber;

        protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;

        [TestCase("0x60006000556000600055", 1612UL, 0, 0)]
        [TestCase("0x60006000556001600055", 20812UL, 0, 0)]
        [TestCase("0x60016000556000600055", 20812UL, 19200, 0)]
        [TestCase("0x60016000556002600055", 20812UL, 0, 0)]
        [TestCase("0x60016000556001600055", 20812UL, 0, 0)]
        [TestCase("0x60006000556000600055", 5812UL, 15000, 1)]
        [TestCase("0x60006000556001600055", 5812UL, 4200, 1)]
        [TestCase("0x60006000556002600055", 5812UL, 0, 1)]
        [TestCase("0x60026000556000600055", 5812UL, 15000, 1)]
        [TestCase("0x60026000556003600055", 5812UL, 0, 1)]
        [TestCase("0x60026000556001600055", 5812UL, 4200, 1)]
        [TestCase("0x60026000556002600055", 5812UL, 0, 1)]
        [TestCase("0x60016000556000600055", 5812UL, 15000, 1)]
        [TestCase("0x60016000556002600055", 5812UL, 0, 1)]
        [TestCase("0x60016000556001600055", 1612UL, 0, 1)]
        [TestCase("0x600160005560006000556001600055", 40818UL, 19200, 0)]
        [TestCase("0x600060005560016000556000600055", 10818UL, 19200, 1)]
        public void Test(string codeHex, ulong gasUsed, long refund, byte originalValue)
        {
            SetupStorage(originalValue);
            TestAllTracerWithOutput receipt = Execute(Bytes.FromHexString(codeHex));
            AssertGas(receipt, gasUsed + GasCostOf.Transaction - Math.Min((gasUsed + GasCostOf.Transaction) / 2, (ulong)refund));
        }

        [TestCase("0x60006000556000600055", 1612UL, 0, 2300UL, true)]
        [TestCase("0x60016000556000600055", 20812UL, 0, 2300UL, true)]
        [TestCase("0x60016000556002600055", 20812UL, 0, 2300UL, true)]
        [TestCase("0x60016000556001600055", 20812UL, 0, 2300UL, true)]
        [TestCase("0x60006000556000600055", 5812UL, 1, 2300UL, true)]
        [TestCase("0x60006000556001600055", 5812UL, 1, 2300UL, true)]
        [TestCase("0x60026000556000600055", 5812UL, 1, 2300UL, true)]
        [TestCase("0x60026000556003600055", 5812UL, 1, 2300UL, true)]
        [TestCase("0x60026000556001600055", 5812UL, 1, 2300UL, true)]
        [TestCase("0x60026000556002600055", 5812UL, 1, 2300UL, true)]
        [TestCase("0x60016000556001600055", 1612UL, 1, 2300UL, true)]
        [TestCase("0x60006000556002600055", 5812UL, 1, 2300UL, true)]
        [TestCase("0x60016000556000600055", 5812UL, 1, 2300UL, false)]
        [TestCase("0x60016000556002600055", 5812UL, 1, 2300UL, false)]
        [TestCase("0x600160005560006000556001600055", 40818UL, 0, 2300UL, false)]
        [TestCase("0x600060005560016000556000600055", 10818UL, 1, 2300UL, false)]
        [TestCase("0x60006000556001600055", 20812UL, 0, 2300UL, false)]
        [TestCase("0x60006000556000600055", 1612UL, 0, 2301UL, false)]
        [TestCase("0x60016000556001600055", 1612UL, 1, 2301UL, false)]
        [TestCase("0x60006000556000600055", 1612UL, 0, 2299UL, true)]
        [TestCase("0x60016000556001600055", 1612UL, 1, 2299UL, true)]
        public void Test_at_stipend_boundary(string codeHex, ulong gasUsed, byte originalValue, ulong stipend, bool outOfGasExpected)
        {
            SetupStorage(originalValue);
            TestAllTracerWithOutput receipt = Execute(BlockNumber, 21000 + gasUsed + stipend - 800, Bytes.FromHexString(codeHex));
            Assert.That(receipt.StatusCode, Is.EqualTo(outOfGasExpected ? 0 : 1));
        }

        private void SetupStorage(byte originalValue)
        {
            TestState.CreateAccount(Recipient, 0);
            TestState.Set(new StorageCell(Recipient, 0), [originalValue]);
            TestState.Commit(MainnetSpecProvider.Instance.GenesisSpec);
        }
    }
}
