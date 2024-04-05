// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Facade.Test
{
    public class TxPoolBridgeTests
    {
        private ITxSender _txSender;
        private ITxPool _txPool;
        private ITxSigner _txSigner;
        private INonceManager _nonceManager;
        private IEthereumEcdsa _ecdsa;

        [SetUp]
        public void SetUp()
        {
            _txPool = Substitute.For<ITxPool>();
            _txSigner = Substitute.For<ITxSigner>();
            _nonceManager = new NonceManager(Substitute.For<IAccountStateProvider>());
            _ecdsa = Substitute.For<IEthereumEcdsa>();
            _txSender = new TxPoolSender(_txPool, new TxSealer(_txSigner, Timestamper.Default), _nonceManager, _ecdsa);
        }

        [Test]
        public void Timestamp_is_set_on_transactions()
        {
            Transaction tx = Build.A.Transaction.WithSenderAddress(TestItem.AddressA).Signed(new EthereumEcdsa(TestBlockchainIds.ChainId, LimboLogs.Instance), TestItem.PrivateKeyA).TestObject;
            _txSender.SendTransaction(tx, TxHandlingOptions.PersistentBroadcast);
            _txPool.Received().SubmitTx(Arg.Is<Transaction>(tx => tx.Timestamp != UInt256.Zero), TxHandlingOptions.PersistentBroadcast);
        }

        [Test]
        public void get_transaction_returns_null_when_transaction_not_found()
        {
            _txPool.TryGetPendingTransaction(TestItem.KeccakA, out Transaction tx);
            tx.Should().Be(null);
        }
    }
}
