// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto
{
    [TestFixture]
    public class EthereumEcdsaTests
    {
        public static IEnumerable<(string, Transaction)> TestCaseSources()
        {
            yield return ("legacy", Build.A.Transaction.SignedAndResolved().TestObject);
            yield return ("access list", Build.A.Transaction.WithChainId(TestBlockchainIds.ChainId).WithType(TxType.AccessList).SignedAndResolved().TestObject);
        }

        [TestCaseSource(nameof(TestCaseSources))]
        public void Signature_verify_test((string Name, Transaction Tx) testCase)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia, LimboLogs.Instance);
            ecdsa.Verify(testCase.Tx.SenderAddress!, testCase.Tx);
        }


        [TestCase(true)]
        [TestCase(false)]
        public void Signature_test_sepolia(bool eip155)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia, LimboLogs.Instance);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.TestObject;
            ecdsa.Sign(key, tx, eip155);
            Address? address = ecdsa.RecoverAddress(tx);
            Assert.That(address, Is.EqualTo(key.Address));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Signature_test_olympic(bool isEip155Enabled)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Mainnet, LimboLogs.Instance);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.TestObject;
            ecdsa.Sign(key, tx, isEip155Enabled);
            Address? address = ecdsa.RecoverAddress(tx);
            Assert.That(address, Is.EqualTo(key.Address));
        }

        [Test]
        public void Sign_goerli()
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Goerli, LimboLogs.Instance);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.TestObject;
            ecdsa.Sign(key, tx, true);
            Address? address = ecdsa.RecoverAddress(tx);
            Assert.That(address, Is.EqualTo(key.Address));
        }

        [Test]
        public void Recover_kovan([Values(false, true)] bool eip155)
        {
            EthereumEcdsa singEcdsa = new(BlockchainIds.Mainnet, LimboLogs.Instance);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.TestObject;
            singEcdsa.Sign(key, tx, eip155);

            EthereumEcdsa recoverEcdsa = new(BlockchainIds.Kovan, LimboLogs.Instance);
            Address? address = recoverEcdsa.RecoverAddress(tx, true);
            Assert.That(address, Is.EqualTo(key.Address));
        }
    }
}
