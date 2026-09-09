// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    /// <summary>
    /// https://gist.github.com/holiman/174548cad102096858583c6fbbb0649a
    /// </summary>
    public class Eip2929Tests : VirtualMachineTestsBase
    {
        protected override ulong BlockNumber => MainnetSpecProvider.BerlinBlockNumber;
        protected override ISpecProvider SpecProvider => MainnetSpecProvider.Instance;

        [TestCase("0x60013f5060023b506003315060f13f5060f23b5060f3315060f23f5060f33b5060f1315032315030315000", 8653ul)]
        [TestCase("0x60006000600060ff3c60006000600060ff3c600060006000303c00", 2835ul)]
        [TestCase("0x60015450601160015560116002556011600255600254600154", 44529ul)]
        [TestCase("0x60008080808060046000f15060008080808060ff6000f15060008080808060ff6000fa50", 2869ul)]
        public void Eip2929_gas_cost(string codeHex, ulong expectedGasExcludingTx)
        {
            TestState.CreateAccount(TestItem.AddressC, 100.Ether);

            byte[] code = Prepare.EvmCode
                .FromCode(codeHex)
                .Done;

            TestAllTracerWithOutput result = Execute(code);
            Assert.That(result.StatusCode, Is.EqualTo(1));
            AssertGas(result, GasCostOf.Transaction + expectedGasExcludingTx);
        }

        private sealed class StorageObservationTracer(bool storage) : TestAllTracerWithOutput
        {
            public override bool IsTracingInstructions => false;
            public override bool IsTracingOpLevelStorage => storage;
            public int StorageReads { get; private set; }

            public override void LoadOperationStorage(Address address, UInt256 storageIndex, ReadOnlySpan<byte> value) => StorageReads++;
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void Storage_observation_flags_are_independent_and_refreshed(bool storage, bool access)
        {
            byte[] code = Bytes.FromHexString("6001545060015450");
            Verify(storage, access);
            Verify(!storage, !access);

            void Verify(bool traceStorage, bool traceAccess)
            {
                StorageObservationTracer tracer = new(traceStorage) { IsTracingAccess = traceAccess };
                Execute(tracer, code);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
                    Assert.That(tracer.StorageReads, Is.EqualTo(traceStorage ? 2 : 0));
                    Assert.That(tracer.GasSpent, Is.EqualTo(traceAccess ? 21210UL : 23210UL));
                }
            }
        }

        private sealed class RefundObservationTracer(bool refunds, bool actions) : TestAllTracerWithOutput
        {
            public override bool IsTracingInstructions => false;
            public override bool IsTracingRefunds => refunds;
            public override bool IsTracingActions => actions;
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void Refund_and_action_flags_are_independent_and_refreshed(bool refunds, bool actions)
        {
            byte[] code = Bytes.FromHexString("60016000556000600055");
            Verify(refunds, actions);
            Verify(!refunds, !actions);

            void Verify(bool traceRefunds, bool traceActions)
            {
                RefundObservationTracer tracer = new(traceRefunds, traceActions) { IsTracingAccess = false };
                Execute(tracer, code);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(tracer.StatusCode, Is.EqualTo(StatusCode.Success));
                    Assert.That(tracer.Refund, Is.EqualTo(traceRefunds ? RefundOf.SSetReversedHotCold : 0));
                    Assert.That(tracer.Actions, Has.Count.EqualTo(traceActions ? 1 : 0));
                }
            }
        }

        protected override TestAllTracerWithOutput CreateTracer()
        {
            TestAllTracerWithOutput tracer = base.CreateTracer();
            tracer.IsTracingAccess = false;
            return tracer;
        }
    }
}
