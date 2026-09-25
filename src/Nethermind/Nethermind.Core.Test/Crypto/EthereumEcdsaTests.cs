// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
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
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            ecdsa.Verify(testCase.Tx.SenderAddress!, testCase.Tx);
        }

        /// <summary>
        /// The wire-bytes recovery must agree with the re-encoding one on every transaction shape, including the
        /// legacy EIP-155 chain id triplet and a pre-155 signature that carries none.
        /// </summary>
        [TestCase(TxType.Legacy, true)]
        [TestCase(TxType.Legacy, false)]
        [TestCase(TxType.AccessList, true)]
        [TestCase(TxType.EIP1559, true)]
        [TestCase(TxType.Blob, true)]
        [TestCase(TxType.SetCode, true)]
        public void TryRecoverPublicKey_from_the_encoding_matches_recovery_from_the_transaction(TxType txType, bool eip155)
        {
            EthereumEcdsa ecdsa = new(TestBlockchainIds.ChainId);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            TransactionBuilder<Transaction> builder = Build.A.Transaction.WithType(txType).WithChainId(TestBlockchainIds.ChainId);

            if (txType == TxType.Blob) builder = builder.WithBlobVersionedHashes(1).WithMaxFeePerBlobGas(1);
            if (txType == TxType.SetCode) builder = builder.WithAuthorizationCodeIfAuthorizationListTx();

            Transaction tx = builder.TestObject;
            ecdsa.Sign(key, tx, isEip155Enabled: eip155);

            byte[] encoded = TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;
            Span<byte> recovered = stackalloc byte[PublicKey.PrefixedLengthInBytes];

            bool result = ecdsa.TryRecoverPublicKey(tx, encoded, recovered);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.True);
                Assert.That(recovered.ToArray(), Is.EqualTo(ecdsa.RecoverPublicKey(tx)!.PrefixedBytes));
                Assert.That(PublicKey.ComputeAddress(recovered[1..]), Is.EqualTo(key.Address));
            }
        }

        [Test]
        public void TryRecoverPublicKey_rejects_an_encoding_of_another_transaction()
        {
            EthereumEcdsa ecdsa = new(TestBlockchainIds.ChainId);
            Transaction tx = Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject;
            Transaction other = Build.A.Transaction.WithNonce(2).SignedAndResolved().TestObject;
            Span<byte> recovered = stackalloc byte[PublicKey.PrefixedLengthInBytes];

            byte[] otherEncoded = TxDecoder.Instance.Encode(other, RlpBehaviors.SkipTypedWrapping).Bytes;

            Assert.That(!ecdsa.TryRecoverPublicKey(tx, otherEncoded, recovered)
                || !recovered.SequenceEqual(ecdsa.RecoverPublicKey(tx)!.PrefixedBytes), Is.True);
        }

        [Test]
        public void Verify_returns_false_for_unsigned_transaction()
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            Transaction tx = Build.A.Transaction.TestObject;

            Assert.That(ecdsa.Verify(TestItem.AddressA, tx), Is.False);
        }

        [Test]
        public void Signature_test_sepolia([Values] bool eip155)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.TestObject;
            ecdsa.Sign(key, tx, eip155);
            Address? address = ecdsa.RecoverAddress(tx);
            Assert.That(address, Is.EqualTo(key.Address));
        }

        [Test]
        public void TryRecoverAddress_recovers_sender_for_signed_transaction()
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.TestObject;
            ecdsa.Sign(key, tx);

            bool result = ecdsa.TryRecoverAddress(tx, out Address? address);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.True);
                Assert.That(address, Is.EqualTo(key.Address));
            }
        }

        [Test]
        public void TryRecoverAddress_returns_false_for_unsigned_transaction()
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            Transaction tx = Build.A.Transaction.TestObject;

            bool result = ecdsa.TryRecoverAddress(tx, out Address? address);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.False);
                Assert.That(address, Is.Null);
            }
        }

        [Test]
        public void Signature_test_sepolia_1559([Values] bool eip155)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).TestObject;
            ecdsa.Sign(key, tx, eip155);
            Address? address = ecdsa.RecoverAddress(tx);
            Assert.That(address, Is.EqualTo(key.Address));
        }

        [Test]
        public void Signature_test_olympic([Values] bool isEip155Enabled)
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

        // Typed txs are served from the hash-keyed sender cache on repeat recovery; legacy txs
        // are excluded (signing hash depends on the ambient chain id)
        [TestCase(TxType.EIP1559, true)]
        [TestCase(TxType.Legacy, false)]
        public void RecoverAddress_repeat_recovery_uses_sender_cache_for_typed_tx_only(TxType txType, bool servedFromCache)
        {
            // The sender cache is a process-wide static; reset it so the miss/hit sequence
            // asserted below cannot depend on what other tests recovered earlier.
            EthereumEcdsaExtensions.ClearSenderCache();

            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            PrivateKey keyA = TestItem.PrivateKeyA;
            PrivateKey keyB = TestItem.PrivateKeyB;
            // Unique content per case so the process-wide cache cannot collide across tests
            static Transaction Create(TxType txType) => Build.A.Transaction
                .WithType(txType)
                .WithNonce(txType == TxType.Legacy ? 0xBEEFUL : 0xC0FFEEUL)
                .TestObject;

            Transaction txA = Create(txType);
            ecdsa.Sign(keyA, txA);
            txA.Hash = txA.CalculateHash();
            Assert.That(ecdsa.RecoverAddress(txA), Is.EqualTo(keyA.Address));

            // Same hash, different signature: a cache hit returns the previously recovered
            // sender, a recompute returns keyB's address
            Transaction txB = Create(txType);
            ecdsa.Sign(keyB, txB);
            txB.Hash = txA.Hash;

            Assert.That(ecdsa.RecoverAddress(txB), Is.EqualTo(servedFromCache ? keyA.Address : keyB.Address));
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
