// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
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
    }
}
