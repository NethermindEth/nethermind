// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Comparers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.TxPool.Collections;
using NSubstitute;
using NUnit.Framework;
using System;

namespace Nethermind.TxPool.Test.Collections
{
    [TestFixture]
    public class TxGasPriceSortedCollectionTests
    {
        private const int Capacity = 16;
        private TxGasPriceSortedCollection _txGasPriceSortedCollection;
        private readonly Transaction[] _transactions = new Transaction[Capacity * 8];

        [SetUp]
        public void Setup()
        {
            IBlockTree blockTree = Substitute.For<IBlockTree>();
            Block block = Build.A.Block.WithNumber(0).TestObject;
            blockTree.Head.Returns(block);
            _txGasPriceSortedCollection = new TxGasPriceSortedCollection();
            for (int i = 0; i < _transactions.Length; i++)
            {
                UInt256 gasPrice = (UInt256)i;
                _transactions[i] = Build.A.Transaction.WithGasPrice(gasPrice)
                    .WithSenderAddress(Address.FromNumber(gasPrice)).TestObject;
            }
        }

        [Test]
        public void Ensure_collection_does_not_exceed_its_capacity()
        {
            for (int i = 0; i < _transactions.Length; i++)
            {
                var tx = _transactions[^(i + 1)];
                _txGasPriceSortedCollection.TryInsert(ref tx);
                Assert.That(_txGasPriceSortedCollection.Count, Is.EqualTo(Math.Min(16, i + 1)));
            }

            for (int i = 0; i < Capacity; i++)
            {
                _txGasPriceSortedCollection.RemoveLast(out TxGasPriceSortedCollection.TxHashGasPricePair? txHashGasPricePair);
                Assert.That(_txGasPriceSortedCollection.Count, Is.EqualTo(Capacity - i - 1));
            }
        }

        [Test]
        public void Ensure_collection_is_sorted_in_descending_order_of_gasPrice()
        {
            int[] _randomGasPrices = [4, 2, 11, 1, 7, 15, 5, 10, 0, 3, 9, 13, 6, 14, 8, 12];

            for (int i = 0; i < Capacity; i++)
            {
                UInt256 gasPrice = (UInt256)_randomGasPrices[i];
                _transactions[i] = Build.A.Transaction.WithGasPrice(gasPrice)
                    .WithSenderAddress(Address.FromNumber(gasPrice)).TestObject;
                _txGasPriceSortedCollection.TryInsert(ref _transactions[i]);
            }

            for (int i = 0; i < Capacity; i++)
            {
                _txGasPriceSortedCollection.RemoveLast(out TxGasPriceSortedCollection.TxHashGasPricePair? txHashGasPricePair);
                UInt256 gasPrice = (UInt256)(i);
                Assert.That(txHashGasPricePair?.GasPrice, Is.EqualTo(gasPrice));
            }
        }

        [Test]
        public void Insert_txs_with_different_hashes_but_same_gasPrices()
        {
            Transaction tx_0 = Build.A.Transaction.WithGasPrice(0).WithNonce(0)
                    .WithSenderAddress(Address.FromNumber(0)).TestObject;
            Transaction tx_1 = Build.A.Transaction.WithGasPrice(0).WithNonce(1)
                    .WithSenderAddress(Address.FromNumber(0)).TestObject;

            _txGasPriceSortedCollection.TryInsert(ref tx_0);
            _txGasPriceSortedCollection.TryInsert(ref tx_1);

            Assert.That(_txGasPriceSortedCollection.Count, Is.EqualTo(2));
        }

        [Test]
        public void Do_not_insert_txs_with_same_hashes_and_same_gasPrices()
        {
            Transaction tx_0 = Build.A.Transaction.WithGasPrice(0).WithNonce(0)
                    .WithSenderAddress(Address.FromNumber(0)).TestObject;
            Transaction tx_1 = Build.A.Transaction.WithGasPrice(0).WithNonce(0)
                    .WithSenderAddress(Address.FromNumber(0)).TestObject;

            _txGasPriceSortedCollection.TryInsert(ref tx_0);
            _txGasPriceSortedCollection.TryInsert(ref tx_1);

            Assert.That(_txGasPriceSortedCollection.Count, Is.EqualTo(1));
        }
    }
}