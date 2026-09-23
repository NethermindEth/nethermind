// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture(0UL)]
    [TestFixture(1UL)]
    [TestFixture(0x0102030405060708UL)]
    [TestFixture(ulong.MaxValue)]
    public class Eip1344Tests(ulong chainId) : VirtualMachineTestsBase
    {
        protected override ulong BlockNumber => MainnetSpecProvider.IstanbulBlockNumber;
        private readonly ISpecProvider _specProvider = new TestSpecProvider(Istanbul.Instance) { ChainId = chainId };
        protected override ISpecProvider SpecProvider => _specProvider;

        [Test]
        public void Chain_id_opcode_puts_expected_value_onto_the_stack([Values] bool traced)
        {
            ulong expectedChainId = SpecProvider.ChainId;

            byte[] code = Prepare.EvmCode
                .Op(Instruction.CHAINID)
                .PushData(0)
                .Op(Instruction.SSTORE)
                .Done;
            ChainIdTracer result = Execute(new ChainIdTracer(traced), code);
            ulong setCost = expectedChainId == 0 ? GasCostOf.SStoreNetMeteredEip2200 : GasCostOf.SSet;
            Assert.That(result.StatusCode, Is.EqualTo(StatusCode.Success));
            AssertGas(result, 21000 + GasCostOf.VeryLow + GasCostOf.Base + setCost);
            byte[] expected = new byte[32];
            BinaryPrimitives.WriteUInt64BigEndian(expected.AsSpan(24), expectedChainId);
            AssertStorage(0, expected);
            Assert.That(result.FirstPush, traced ? Is.EqualTo(expected) : Is.Null);
        }

        private sealed class ChainIdTracer(bool traced) : TestAllTracerWithOutput
        {
            public override bool IsTracingInstructions => traced;
            public byte[]? FirstPush { get; private set; }
            public override void ReportStackPush(in ReadOnlySpan<byte> stackItem) => FirstPush ??= stackItem.ToArray();
        }
    }
}
