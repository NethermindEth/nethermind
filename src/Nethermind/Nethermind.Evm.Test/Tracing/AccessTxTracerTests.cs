// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [TestFixture]
    public class AccessTxTracerTests : VirtualMachineTestsBase
    {
        [Test]
        public void Records_get_correct_accessed_addresses()
        {
            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            TestState.Commit(Berlin.Instance);

            (AccessTxTracer tracer, _, _) = ExecuteAndTraceAccessCall(SenderRecipientAndMiner.Default, code);

            IEnumerable<Address> addressesAccessed = tracer.AccessList!.AsEnumerable().Select(tuples => tuples.Address).ToArray();
            IEnumerable<Address> expected = new[] {
                SenderRecipientAndMiner.Default.Sender, SenderRecipientAndMiner.Default.Recipient, TestItem.AddressC
            };

            Assert.IsNotEmpty(addressesAccessed);
            addressesAccessed.Should().BeEquivalentTo(expected);
        }

        [Test]
        public void Records_get_correct_accessed_keys()
        {
            byte[] code = Prepare.EvmCode
                .PushData("0x01")
                .PushData("0x69")
                .Op(Instruction.SSTORE)
                .Done;

            (AccessTxTracer tracer, _, _) = ExecuteAndTraceAccessCall(SenderRecipientAndMiner.Default, code);

            IEnumerable<(Address, IEnumerable<UInt256>)> accessedData = tracer.AccessList!.AsEnumerable().ToArray();

            Assert.IsNotEmpty(accessedData);
            accessedData.Should().BeEquivalentTo(
                new []
                {
                    (SenderRecipientAndMiner.Default.Sender, new UInt256[] { }),
                    (SenderRecipientAndMiner.Default.Recipient, new UInt256[] { 105 })
                });
        }

        protected override ISpecProvider SpecProvider => new TestSpecProvider(Berlin.Instance);

        protected (AccessTxTracer trace, Block block, Transaction transaction) ExecuteAndTraceAccessCall(SenderRecipientAndMiner addresses, params byte[] code)
        {
            (Block block, Transaction transaction) = PrepareTx(BlockNumber, 100000, code, addresses);
            AccessTxTracer tracer = new();
            _processor.Execute(transaction, block.Header, tracer);
            return (tracer, block, transaction);
        }
    }
}
