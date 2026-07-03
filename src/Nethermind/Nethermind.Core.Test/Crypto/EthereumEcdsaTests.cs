// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
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
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            ecdsa.Verify(testCase.Tx.SenderAddress!, testCase.Tx);
        }


        [TestCase(true)]
        [TestCase(false)]
        public void Signature_test_sepolia(bool eip155)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.TestObject;
            ecdsa.Sign(key, tx, eip155);
            Address? address = ecdsa.RecoverAddress(tx);
            Assert.That(address, Is.EqualTo(key.Address));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Signature_test_sepolia_1559(bool eip155)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).TestObject;
            ecdsa.Sign(key, tx, eip155);
            Address? address = ecdsa.RecoverAddress(tx);
            Assert.That(address, Is.EqualTo(key.Address));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Signature_test_olympic(bool isEip155Enabled)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Mainnet);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.TestObject;
            ecdsa.Sign(key, tx, isEip155Enabled);
            Address? address = ecdsa.RecoverAddress(tx);
            Assert.That(address, Is.EqualTo(key.Address));
        }

        [TestCase(TxType.Legacy, true)]
        [TestCase(TxType.Legacy, false)]
        [TestCase(TxType.AccessList, true)]
        [TestCase(TxType.EIP1559, true)]
        public void RecoverPublicKey_transaction_recovers_signer_public_key(TxType txType, bool isEip155Enabled)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.WithType(txType).TestObject;

            ecdsa.Sign(key, tx, isEip155Enabled);

            PublicKey? publicKey = ecdsa.RecoverPublicKey(tx);

            Assert.That(publicKey, Is.EqualTo(key.PublicKey));
        }

        [Test]
        public void Sign_generic_network()
        {
            // maybe make random id so it captures the idea that signature should work irrespective of chain
            EthereumEcdsa ecdsa = new(BlockchainIds.GenericNonRealNetwork);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.TestObject;
            ecdsa.Sign(key, tx, true);
            Address? address = ecdsa.RecoverAddress(tx);
            Assert.That(address, Is.EqualTo(key.Address));
        }

        [Test]
        public void RecoverAddress_typed_tx_repeat_is_served_from_sender_cache()
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            PrivateKey keyA = TestItem.PrivateKeyA;
            PrivateKey keyB = TestItem.PrivateKeyB;
            // Unique content so the process-wide cache cannot collide with other tests
            static Transaction Create() => Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithNonce(0xC0FFEE)
                .TestObject;

            Transaction txA = Create();
            ecdsa.Sign(keyA, txA);
            txA.Hash = txA.CalculateHash();
            Assert.That(ecdsa.RecoverAddress(txA), Is.EqualTo(keyA.Address));

            // Same hash, different signature: a cache hit returns the previously recovered sender,
            // a recompute would return keyB's address
            Transaction txB = Create();
            ecdsa.Sign(keyB, txB);
            txB.Hash = txA.Hash;

            Assert.That(ecdsa.RecoverAddress(txB), Is.EqualTo(keyA.Address));
        }

        [Test]
        public void RecoverAddress_legacy_tx_is_not_served_from_sender_cache()
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            PrivateKey keyA = TestItem.PrivateKeyA;
            PrivateKey keyB = TestItem.PrivateKeyB;
            static Transaction Create() => Build.A.Transaction
                .WithNonce(0xBEEF)
                .TestObject;

            Transaction txA = Create();
            ecdsa.Sign(keyA, txA);
            txA.Hash = txA.CalculateHash();
            Assert.That(ecdsa.RecoverAddress(txA), Is.EqualTo(keyA.Address));

            Transaction txB = Create();
            ecdsa.Sign(keyB, txB);
            txB.Hash = txA.Hash;

            Assert.That(ecdsa.RecoverAddress(txB), Is.EqualTo(keyB.Address));
        }

        [Test]
        [Repeat(3)]
        public void RecoverAddress_AuthorizationTupleOfDifferentSize_RecoversAddressCorrectly()
        {
            PrivateKey signer = Build.A.PrivateKey.TestObject;
            AuthorizationTuple authorizationTuple = new EthereumEcdsa(BlockchainIds.GenericNonRealNetwork)
                .Sign(signer,
                TestContext.CurrentContext.Random.NextULong(),
                Build.A.Address.TestObjectInternal,
                TestContext.CurrentContext.Random.NextULong());

            EthereumEcdsa ecdsa = new(BlockchainIds.GenericNonRealNetwork);

            Address? authority = ecdsa.RecoverAddress(authorizationTuple);

            Assert.That(authority, Is.EqualTo(signer.Address));
        }
    }
}
