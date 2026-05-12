// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
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

            (AccessTxTracer tracer, _, _) = ExecuteAndTraceAccessCall(SenderRecipientAndMiner.Default, code);

            IEnumerable<Address> addressesAccessed = tracer.AccessList!.Select(static tuples => tuples.Address);
            IEnumerable<Address> expected = new[] {
                SenderRecipientAndMiner.Default.Sender, SenderRecipientAndMiner.Default.Recipient, TestItem.AddressC
            };

            Assert.That(addressesAccessed, Is.EquivalentTo(expected));
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

            Assert.That(tracer.AccessList!, Is.EquivalentTo(new[]
                {
                    (SenderRecipientAndMiner.Default.Sender, System.Array.Empty<UInt256>()),
                    (SenderRecipientAndMiner.Default.Recipient, new UInt256[] { 105 })
                }));
        }

        public static IEnumerable OptimizedAddressCases
        {
            get
            {
                yield return new TestCaseData(
                    new Address[] { TestItem.AddressA, TestItem.AddressB },
                    System.Array.Empty<Address>());
                yield return new TestCaseData(
                    new Address[] { TestItem.AddressB },
                    new[] { TestItem.AddressA });
                yield return new TestCaseData(
                    new Address[] { TestItem.AddressA },
                    new[] { TestItem.AddressB });
            }
        }

        [TestCaseSource(nameof(OptimizedAddressCases))]
        public void ReportAccess_AddressIsSetToOptimizedWithNoStorageCells_OnlyAddressesNotOptimizedIsInTheAccessList(IEnumerable<Address> optimized, IEnumerable<Address> expected)
        {
            JournalSet<Address> accessedAddresses = new(Address.EqualityComparer) { TestItem.AddressA, TestItem.AddressB };
            AccessTxTracer sut = new(optimized.ToArray());

            sut.ReportAccess(accessedAddresses, []);

            Assert.That(sut.AccessList.Select(static a => a.Address).ToArray(), Is.EquivalentTo(expected));
        }

        [Test]
        public void ReportAccess_AddressAIsSetToOptimizedAndHasStorageCell_AddressAAndBIsInTheAccessList()
        {
            JournalSet<Address> accessedAddresses = new(Address.EqualityComparer) { TestItem.AddressA, TestItem.AddressB };
            JournalSet<StorageCell> accessedStorageCells = new(StorageCell.EqualityComparer) { new StorageCell(TestItem.AddressA, 0) };
            AccessTxTracer sut = new(TestItem.AddressA);

            sut.ReportAccess(accessedAddresses, accessedStorageCells);

            Assert.That(sut.AccessList.Select(static x => x.Address).ToArray(), Is.EquivalentTo(new[] { TestItem.AddressA, TestItem.AddressB }));
        }

        [Test]
        public void ReportAccess_AddressAIsSetToOptimizedAndHasStorageCell_AccessListHasCorrectStorageCell()
        {
            JournalSet<Address> accessedAddresses = new(Address.EqualityComparer) { TestItem.AddressA, TestItem.AddressB };
            JournalSet<StorageCell> accessedStorageCells = new(StorageCell.EqualityComparer) { new StorageCell(TestItem.AddressA, 1) };
            AccessTxTracer sut = new(TestItem.AddressA);

            sut.ReportAccess(accessedAddresses, accessedStorageCells);

            Assert.That(sut.AccessList.Select(static x => x.StorageKeys), Has.Exactly(1).Contains(new UInt256(1)));
        }

        [Test]
        public void Reverted_call_target_address_is_still_captured_in_access_list()
        {
            // Code deployed at recipient: CALL AddressC, then REVERT
            byte[] code = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.REVERT)
                .Done;

            AccessList list = ExecuteRevertedFrameScenario(code);

            Assert.That(
                list.Select(static t => t.Address),
                Has.Member(TestItem.AddressC),
                "addresses accessed inside reverted frames must survive for eth_createAccessList");
        }

        [Test]
        public void Reverted_sub_frame_sload_storage_key_is_still_captured_in_access_list()
        {
            // Code at AddressC: SLOAD slot 7 then REVERT
            byte[] addressCCode = Prepare.EvmCode
                .PushData(7)
                .Op(Instruction.SLOAD)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.REVERT)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 0);
            TestState.InsertCode(TestItem.AddressC, addressCCode, SpecProvider.GenesisSpec);
            TestState.Commit(SpecProvider.GenesisSpec);
            TestState.CommitTree(0);

            // Recipient code: CALL AddressC then STOP
            byte[] recipientCode = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            AccessList list = ExecuteRevertedFrameScenario(recipientCode);

            // AddressC slot 7 must appear despite the REVERT inside AddressC's sub-frame
            (Address Address, AccessList.StorageKeysEnumerable StorageKeys)[] addressCEntries =
                list.Where(static e => e.Address == TestItem.AddressC).ToArray();
            Assert.That(addressCEntries, Has.Length.EqualTo(1));
            Assert.That(
                addressCEntries[0].StorageKeys,
                Has.Member(new UInt256(7)),
                "storage cells first accessed inside a reverted sub-frame must still be captured");
        }

        [Test]
        public void Outer_committed_and_inner_reverted_call_both_captured_in_access_list()
        {
            TestState.CreateAccount(TestItem.AddressE, 0);
            TestState.InsertCode(TestItem.AddressE, Prepare.EvmCode.Op(Instruction.STOP).Done, SpecProvider.GenesisSpec);

            // AddressC code: CALL AddressE then REVERT
            byte[] addressCCode = Prepare.EvmCode
                .Call(TestItem.AddressE, 20000)
                .PushData(0)
                .PushData(0)
                .Op(Instruction.REVERT)
                .Done;

            TestState.CreateAccount(TestItem.AddressC, 0);
            TestState.InsertCode(TestItem.AddressC, addressCCode, SpecProvider.GenesisSpec);
            TestState.Commit(SpecProvider.GenesisSpec);
            TestState.CommitTree(0);

            // Recipient code: CALL AddressC (succeeds at EVM level but AddressC reverts internally)
            byte[] recipientCode = Prepare.EvmCode
                .Call(TestItem.AddressC, 50000)
                .Op(Instruction.STOP)
                .Done;

            AccessList list = ExecuteRevertedFrameScenario(recipientCode);

            Address[] addresses = list.Select(static t => t.Address).ToArray();
            Assert.That(addresses, Has.Member(TestItem.AddressC), "committed outer CALL target must be in access list");
            Assert.That(addresses, Has.Member(TestItem.AddressE), "address accessed inside inner reverted frame must still be in access list");
        }

        protected override ISpecProvider SpecProvider => new TestSpecProvider(Berlin.Instance);

        protected (AccessTxTracer trace, Block block, Transaction transaction) ExecuteAndTraceAccessCall(SenderRecipientAndMiner addresses, params byte[] code)
        {
            (Block block, Transaction transaction) = PrepareTx(BlockNumber, 100000, code, addresses);
            AccessTxTracer tracer = new();
            _processor.Execute(transaction, new BlockExecutionContext(block.Header, Spec), tracer);
            return (tracer, block, transaction);
        }

        private AccessList ExecuteRevertedFrameScenario(byte[] recipientCode)
        {
            (Block block, Transaction tx) = PrepareTx(BlockNumber, 100000, recipientCode, SenderRecipientAndMiner.Default);
            AccessTxTracer tracer = new(SenderRecipientAndMiner.Default.Sender, SenderRecipientAndMiner.Default.Recipient, SenderRecipientAndMiner.Default.Miner);
            _processor.Execute(tx, new BlockExecutionContext(block.Header, Spec), tracer);
            return tracer.AccessList!;
        }
    }
}
