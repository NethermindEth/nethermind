// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions
{
    public class GeneratedTxSourceSealerTests
    {
        [Test]
        public void transactions_are_addable_to_block_after_sealing()
        {
            int chainId = 5;
            BlockHeader blockHeader = Build.A.BlockHeader.TestObject;
            GeneratedTransaction tx1 = Build.A.GeneratedTransaction.WithSenderAddress(TestItem.AddressA).TestObject;
            GeneratedTransaction tx2 = Build.A.GeneratedTransaction.WithSenderAddress(TestItem.AddressA).TestObject;
            ITimestamper timestamper = Substitute.For<ITimestamper>();
            IStateReader stateReader = Substitute.For<IStateReader>();
            Address nodeAddress = TestItem.AddressA;

            UInt256 expectedNonce = 10;
            stateReader.GetAccount(blockHeader.StateRoot, nodeAddress).Returns(Account.TotallyEmpty.WithChangedNonce(expectedNonce));

            ulong expectedTimeStamp = 100;
            timestamper.UnixTime.Returns(UnixTime.FromSeconds(expectedTimeStamp));

            int gasLimit = 200;
            ITxSource innerTxSource = Substitute.For<ITxSource>();
            innerTxSource.GetTransactions(blockHeader, gasLimit).Returns(new[] { tx1, tx2 });

            TxSealer txSealer = new(new Signer((ulong)chainId, Build.A.PrivateKey.TestObject, LimboLogs.Instance), timestamper);
            GeneratedTxSource transactionFiller = new(innerTxSource, txSealer, stateReader, LimboLogs.Instance);

            Transaction[] sealedTxs = transactionFiller.GetTransactions(blockHeader, gasLimit).ToArray();
            Transaction sealedTx1 = sealedTxs.First();
            Transaction sealedTx2 = sealedTxs.Skip(1).First();

            sealedTx1.IsSigned.Should().BeTrue();
            sealedTx1.Nonce.Should().Be(expectedNonce);
            sealedTx1.Hash.Should().Be(tx1.CalculateHash());
            sealedTx1.Timestamp.Should().Be(expectedTimeStamp);

            sealedTx2.IsSigned.Should().BeTrue();
            sealedTx2.Nonce.Should().Be(expectedNonce + 1);
            sealedTx2.Hash.Should().NotBe(tx1.CalculateHash());
            sealedTx2.Timestamp.Should().Be(expectedTimeStamp);

        }
    }
}
