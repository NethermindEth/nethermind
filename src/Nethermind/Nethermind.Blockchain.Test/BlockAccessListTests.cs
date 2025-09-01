// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain.BeaconBlockRoot;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Test.Validators;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
using NUnit.Framework;

//move all to correct folder
namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class BlockAccessListTests() : TransactionProcessorTests(true)
    {
        [Test]
        public void Empty_account_changes()
        {
            Block block = Build.A.Block.WithTransactions(Build.A.Transaction.TestObject).TestObject;

            BlockAccessTracer tracer = new();
            tracer.StartNewBlockTrace(block);
            tracer.StartNewTxTrace(block.Transactions[0]);
            tracer.MarkAsSuccess(TestItem.AddressA, 100, [], [], TestItem.KeccakF);

            Assert.That(tracer.BlockAccessList.AccountChanges, Has.Count.EqualTo(0));
        }

        [Test]
        public void Balance_and_nonce_changes()
        {
            ulong gasPrice = 2;
            long gasLimit = 100000;
            Transaction tx = Build.A.Transaction
                .WithTo(TestItem.AddressB)
                .WithSenderAddress(TestItem.AddressA)
                .WithValue(0)
                .WithGasPrice(gasPrice)
                .WithGasLimit(gasLimit)
                .TestObject;

            Block block = Build.A.Block
                .WithTransactions(tx)
                .WithBaseFeePerGas(1)
                .WithBeneficiary(TestItem.AddressC).TestObject;

            BlockReceiptsTracer blockReceiptsTracer = new();
            BlockAccessTracer accessTracer = new();
            blockReceiptsTracer.SetOtherTracer(accessTracer);
            Execute(tx, block, blockReceiptsTracer);

            SortedDictionary<Address, AccountChanges> accountChanges = accessTracer.BlockAccessList.AccountChanges;
            Assert.That(accountChanges, Has.Count.EqualTo(3));

            List<BalanceChange> senderBalanceChanges = accountChanges[TestItem.AddressA].BalanceChanges;
            List<NonceChange> senderNonceChanges = accountChanges[TestItem.AddressA].NonceChanges;
            List<BalanceChange> toBalanceChanges = accountChanges[TestItem.AddressB].BalanceChanges;
            List<BalanceChange> beneficiaryBalanceChanges = accountChanges[TestItem.AddressC].BalanceChanges;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(senderBalanceChanges, Has.Count.EqualTo(1));
                Assert.That(senderBalanceChanges[0].PostBalance, Is.EqualTo(AccountBalance - gasPrice * GasCostOf.Transaction));

                Assert.That(senderNonceChanges, Has.Count.EqualTo(1));
                Assert.That(senderNonceChanges[0].NewNonce, Is.EqualTo(1));

                // zero balance change should not be recorded
                Assert.That(toBalanceChanges, Is.Empty);

                Assert.That(beneficiaryBalanceChanges, Has.Count.EqualTo(1));
                Assert.That(beneficiaryBalanceChanges[0].PostBalance, Is.EqualTo(new UInt256(GasCostOf.Transaction)));
            }
        }

        [Test]
        public void Can_encode_and_decode()
        {
            StorageChange storageChange = new()
            {
                BlockAccessIndex = 10,
                NewValue = new([.. Enumerable.Repeat<byte>(50, 32)])
            };
            byte[] storageChangeBytes = Rlp.Encode(storageChange, RlpBehaviors.None).Bytes;
            StorageChange storageChangeDecoded = Rlp.Decode<StorageChange>(storageChangeBytes, RlpBehaviors.None);
            Assert.That(storageChange, Is.EqualTo(storageChangeDecoded));

            SlotChanges slotChanges = new()
            {
                Slot = [.. Enumerable.Repeat<byte>(100, 32)],
                Changes = [storageChange, storageChange]
            };
            byte[] slotChangesBytes = Rlp.Encode(slotChanges, RlpBehaviors.None).Bytes;
            SlotChanges slotChangesDecoded = Rlp.Decode<SlotChanges>(slotChangesBytes, RlpBehaviors.None);
            Assert.That(slotChanges, Is.EqualTo(slotChangesDecoded));

            StorageRead storageRead = new(new Bytes32([.. Enumerable.Repeat<byte>(50, 32)]));
            byte[] storageReadBytes = Rlp.Encode(storageRead, RlpBehaviors.None).Bytes;
            StorageRead storageReadDecoded = Rlp.Decode<StorageRead>(storageReadBytes, RlpBehaviors.None);
            Assert.That(storageRead, Is.EqualTo(storageReadDecoded));

            BalanceChange balanceChange = new()
            {
                BlockAccessIndex = 10,
                PostBalance = 0
            };
            byte[] balanceChangeBytes = Rlp.Encode(balanceChange, RlpBehaviors.None).Bytes;
            BalanceChange balanceChangeDecoded = Rlp.Decode<BalanceChange>(balanceChangeBytes, RlpBehaviors.None);
            Assert.That(balanceChange, Is.EqualTo(balanceChangeDecoded));

            NonceChange nonceChange = new()
            {
                BlockAccessIndex = 10,
                NewNonce = 0
            };
            byte[] nonceChangeBytes = Rlp.Encode(nonceChange, RlpBehaviors.None).Bytes;
            NonceChange nonceChangeDecoded = Rlp.Decode<NonceChange>(nonceChangeBytes, RlpBehaviors.None);
            Assert.That(nonceChange, Is.EqualTo(nonceChangeDecoded));

            CodeChange codeChange = new()
            {
                BlockAccessIndex = 10,
                NewCode = [0, 50]
            };
            byte[] codeChangeBytes = Rlp.Encode(codeChange, RlpBehaviors.None).Bytes;
            CodeChange codeChangeDecoded = Rlp.Decode<CodeChange>(codeChangeBytes, RlpBehaviors.None);
            Assert.That(codeChange, Is.EqualTo(codeChangeDecoded));

            SortedDictionary<byte[], SlotChanges> storageChangesDict = new()
            {
                { slotChanges.Slot, slotChanges }
            };

            AccountChanges accountChanges = new()
            {
                Address = TestItem.AddressA,
                StorageChanges = storageChangesDict,
                StorageReads = [storageRead, storageRead],
                BalanceChanges = [balanceChange, balanceChange],
                NonceChanges = [nonceChange, nonceChange],
                CodeChanges = [codeChange]
            };
            byte[] accountChangesBytes = Rlp.Encode(accountChanges, RlpBehaviors.None).Bytes;
            AccountChanges accountChangesDecoded = Rlp.Decode<AccountChanges>(accountChangesBytes, RlpBehaviors.None);
            Assert.That(accountChanges, Is.EqualTo(accountChangesDecoded));

            SortedDictionary<Address, AccountChanges> accountChangesDict = new()
            {
                { accountChanges.Address, accountChanges }
            };

            BlockAccessList blockAccessList = new()
            {
                AccountChanges = accountChangesDict
            };
            byte[] blockAccessListBytes = Rlp.Encode(blockAccessList, RlpBehaviors.None).Bytes;
            BlockAccessList blockAccessListDecoded = Rlp.Decode<BlockAccessList>(blockAccessListBytes, RlpBehaviors.None);
            Assert.That(blockAccessList, Is.EqualTo(blockAccessListDecoded));
        }

        [Test]
        public void System_contracts_and_withdrawals()
        {
            BlockProcessor processor = new(HoleskySpecProvider.Instance,
                TestBlockValidator.AlwaysValid,
                NoBlockRewards.Instance,
                new BlockProcessor.BlockValidationTransactionsExecutor(new ExecuteTransactionProcessorAdapter(_transactionProcessor), _stateProvider),
                _stateProvider,
                NullReceiptStorage.Instance,
                new BeaconBlockRootHandler(_transactionProcessor, _stateProvider),
                Substitute.For<IBlockhashStore>(), // create dummy?
                LimboLogs.Instance,
                new WithdrawalProcessor(_stateProvider, LimboLogs.Instance),
                new ExecutionRequestsProcessor(_transactionProcessor));

            // todo: just use test blockchain?
            _stateProvider.CreateAccount(Eip4788Constants.BeaconRootsAddress, 10);
            _stateProvider.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, 0, Eip7002TestConstants.Nonce);
            _stateProvider.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, Eip7002TestConstants.CodeHash, Eip7002TestConstants.Code, Prague.Instance);
            _stateProvider.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, 0, Eip7251TestConstants.Nonce);
            _stateProvider.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, Eip7251TestConstants.CodeHash, Eip7251TestConstants.Code, Prague.Instance);

            ulong gasPrice = 2;
            long gasLimit = 100000;
            Transaction tx = Build.A.Transaction
                .WithTo(TestItem.AddressB)
                .WithSenderAddress(TestItem.AddressA)
                .WithValue(0)
                .WithGasPrice(gasPrice)
                .WithGasLimit(gasLimit)
                .TestObject;

            Block block = Build.A.Block
                .WithParentBeaconBlockRoot(Hash256.Zero)
                .WithNumber(100)
                .WithTransactions(tx)
                .WithBaseFeePerGas(1)
                .WithBeneficiary(TestItem.AddressC).TestObject;

            // BlockReceiptsTracer blockReceiptsTracer = new();
            // BlockAccessTracer accessTracer = new();
            // blockReceiptsTracer.SetOtherTracer(accessTracer);
            // Execute(tx, block, blockReceiptsTracer);

            OverridableReleaseSpec spec = new(Prague.Instance)
            {
                IsEip7928Enabled = true
            };
            (Block processedBlock, TxReceipt[] _) = processor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, spec, CancellationToken.None);

            BlockAccessList blockAccessList = Rlp.Decode<BlockAccessList>(processedBlock.BlockAccessList);
            SortedDictionary<Address, AccountChanges> accountChanges = blockAccessList.AccountChanges;
            Assert.That(accountChanges, Has.Count.EqualTo(3));

            List<BalanceChange> senderBalanceChanges = accountChanges[TestItem.AddressA].BalanceChanges;
            List<NonceChange> senderNonceChanges = accountChanges[TestItem.AddressA].NonceChanges;
            List<BalanceChange> toBalanceChanges = accountChanges[TestItem.AddressB].BalanceChanges;
            List<BalanceChange> beneficiaryBalanceChanges = accountChanges[TestItem.AddressC].BalanceChanges;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(senderBalanceChanges, Has.Count.EqualTo(1));
                Assert.That(senderBalanceChanges[0].PostBalance, Is.EqualTo(AccountBalance - gasPrice * GasCostOf.Transaction));

                Assert.That(senderNonceChanges, Has.Count.EqualTo(1));
                Assert.That(senderNonceChanges[0].NewNonce, Is.EqualTo(1));

                // zero balance change should not be recorded
                Assert.That(toBalanceChanges, Is.Empty);

                Assert.That(beneficiaryBalanceChanges, Has.Count.EqualTo(1));
                Assert.That(beneficiaryBalanceChanges[0].PostBalance, Is.EqualTo(new UInt256(GasCostOf.Transaction)));
            }
        }
    }
}
