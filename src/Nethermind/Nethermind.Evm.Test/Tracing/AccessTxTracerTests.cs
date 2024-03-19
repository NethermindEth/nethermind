// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;
using static Nethermind.Core.Eip2930.AccessList;

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

            IEnumerable<Address> addressesAccessed = tracer.AccessList!.Select(tuples => tuples.Address);
            IEnumerable<Address> expected = new[] {
                SenderRecipientAndMiner.Default.Sender, SenderRecipientAndMiner.Default.Recipient, TestItem.AddressC
            };

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

            tracer.AccessList!.Should().BeEquivalentTo(
                new[]
                {
                    (SenderRecipientAndMiner.Default.Sender, new UInt256[] { }),
                    (SenderRecipientAndMiner.Default.Recipient, new UInt256[] { 105 })
                });
        }

        public static IEnumerable OptimizedAddressCases
        {
            get
            {
                yield return new TestCaseData(
                    new Address[] { TestItem.AddressA, TestItem.AddressB },
                    new Address[] { });
                yield return new TestCaseData(
                    new Address[] { TestItem.AddressB },
                    new[] { TestItem.AddressA });
                yield return new TestCaseData(
                    new Address[] { TestItem.AddressA },
                    new[] { TestItem.AddressB });
            }
        }

        [TestCaseSource(nameof(OptimizedAddressCases))]
        public void ReportAccess_AddressIsSetToOptmizedWithNoStorageCells_OnlyAddressesNotOptimizedIsInTheAccesslist(IEnumerable<Address> optimized, IEnumerable<Address> expected)
        {
            JournalSet<Address> accessedAddresses = [TestItem.AddressA, TestItem.AddressB];
            JournalSet<StorageCell> accessedStorageCells = [];
            AccessTxTracer sut = new(optimized.ToArray());

            sut.ReportAccess(accessedAddresses, accessedStorageCells);

            Assert.That(sut.AccessList.Select(a => a.Address).ToArray(), Is.EquivalentTo(expected));
        }

        [Test]
        public void ReportAccess_AddressAIsSetToOptmizedAndHasStorageCell_AddressAAndBIsInTheAccesslist()
        {
            JournalSet<Address> accessedAddresses = [TestItem.AddressA, TestItem.AddressB];
            JournalSet<StorageCell> accessedStorageCells = [new StorageCell(TestItem.AddressA, 0)];
            AccessTxTracer sut = new(TestItem.AddressA);

            sut.ReportAccess(accessedAddresses, accessedStorageCells);

            Assert.That(sut.AccessList.Select(x => x.Address).ToArray(), Is.EquivalentTo(new[] { TestItem.AddressA, TestItem.AddressB }));
        }

        [Test]
        public void ReportAccess_AddressAIsSetToOptmizedAndHasStorageCell_AccesslistHasCorrectStorageCell()
        {
            JournalSet<Address> accessedAddresses = [TestItem.AddressA, TestItem.AddressB];
            JournalSet<StorageCell> accessedStorageCells = [new StorageCell(TestItem.AddressA, 1)];
            AccessTxTracer sut = new(TestItem.AddressA);

            sut.ReportAccess(accessedAddresses, accessedStorageCells);

            Assert.That(sut.AccessList.Select(x => x.StorageKeys), Has.Exactly(1).Contains(new UInt256(1)));
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
