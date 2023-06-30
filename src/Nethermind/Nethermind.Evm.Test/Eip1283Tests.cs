// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip1283Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => 1;

        protected override ISpecProvider SpecProvider => new CustomSpecProvider(
            ((ForkActivation)0, Byzantium.Instance),((ForkActivation)1, Constantinople.Instance));

        [TestCase("0x60006000556000600055", 412, 0, 0)]
        [TestCase("0x60006000556001600055", 20212, 0, 0)]
        [TestCase("0x60016000556000600055", 20212, 19800, 0)]
        [TestCase("0x60016000556002600055", 20212, 0, 0)]
        [TestCase("0x60016000556001600055", 20212, 0, 0)]
        [TestCase("0x60006000556000600055", 5212, 15000, 1)]
        [TestCase("0x60006000556001600055", 5212, 4800, 1)]
        [TestCase("0x60006000556002600055", 5212, 0, 1)]
        [TestCase("0x60026000556000600055", 5212, 15000, 1)]
        [TestCase("0x60026000556003600055", 5212, 0, 1)]
        [TestCase("0x60026000556001600055", 5212, 4800, 1)]
        [TestCase("0x60026000556002600055", 5212, 0, 1)]
        [TestCase("0x60016000556000600055", 5212, 15000, 1)]
        [TestCase("0x60016000556002600055", 5212, 0, 1)]
        [TestCase("0x60016000556001600055", 412, 0, 1)]
        [TestCase("0x600160005560006000556001600055", 40218, 19800, 0)]
        [TestCase("0x600060005560016000556000600055", 10218, 19800, 1)]
        public void Test(string codeHex, long gasUsed, long refund, byte originalValue)
        {
            TestState.CreateAccount(Recipient, 0);
            TestState.Set(new StorageCell(Recipient, 0), new[] { originalValue });
            TestState.Commit(MainnetSpecProvider.Instance.GenesisSpec);

            TestAllTracerWithOutput receipt = Execute(Bytes.FromHexString(codeHex));
            AssertGas(receipt, gasUsed + GasCostOf.Transaction - Math.Min((gasUsed + GasCostOf.Transaction) / 2, refund));
        }
    }
}
