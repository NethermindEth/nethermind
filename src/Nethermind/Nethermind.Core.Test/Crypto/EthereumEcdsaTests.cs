// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using NSubstitute;
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
        /// <remarks>
        /// With <paramref name="useSignatureChainId"/> the recovering instance runs on another chain, as a stateless
        /// guest does when the spec skips chain id validation, so only the signature's own chain id recovers the key.
        /// </remarks>
        [TestCase(TxType.Legacy, true)]
        [TestCase(TxType.Legacy, false)]
        [TestCase(TxType.Legacy, true, true)]
        [TestCase(TxType.AccessList, true)]
        [TestCase(TxType.EIP1559, true)]
        [TestCase(TxType.EIP1559, true, true)]
        [TestCase(TxType.Blob, true)]
        [TestCase(TxType.SetCode, true)]
        [TestCase(TxType.Legacy, true, false, 2048)]
        [TestCase(TxType.EIP1559, true, false, 2048)]
        public void TryRecoverPublicKey_from_the_encoding_matches_recovery_from_the_transaction(
            TxType txType, bool eip155, bool useSignatureChainId = false, int dataLength = 0)
        {
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = BuildSigned(txType, key, eip155, dataLength);
            EthereumEcdsa ecdsa = new(useSignatureChainId ? BlockchainIds.Sepolia : TestBlockchainIds.ChainId);

            byte[] encoded = TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;
            Span<byte> recovered = stackalloc byte[PublicKey.PrefixedLengthInBytes];

            bool result = ecdsa.TryRecoverPublicKey(tx, encoded, recovered, useSignatureChainId);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(result, Is.True);
                Assert.That(recovered, Is.SequenceEqualTo(ecdsa.RecoverPublicKey(tx, useSignatureChainId)!.PrefixedBytes));
                Assert.That(PublicKey.ComputeAddress(recovered[1..]), Is.EqualTo(key.Address));
            }
        }

        public enum Malformation { WrongTypeByte, Empty, ByteString }

        /// <summary>Bytes the signed payload cannot be located in fall back to encoding the transaction.</summary>
        [TestCase(TxType.EIP1559, Malformation.WrongTypeByte)]
        [TestCase(TxType.EIP1559, Malformation.Empty)]
        [TestCase(TxType.EIP1559, Malformation.ByteString)]
        [TestCase(TxType.Legacy, Malformation.Empty)]
        [TestCase(TxType.Legacy, Malformation.ByteString)]
        public void TryRecoverPublicKey_falls_back_to_encoding_when_the_payload_cannot_be_located(TxType txType, Malformation malformation)
        {
            EthereumEcdsa ecdsa = new(TestBlockchainIds.ChainId);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = BuildSigned(txType, key, eip155: true);
            byte[] typePrefix = txType == TxType.Legacy ? [] : [(byte)txType];

            byte[] encoded = malformation switch
            {
                Malformation.WrongTypeByte => [(byte)TxType.AccessList, .. TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes[1..]],
                Malformation.Empty => [],
                _ => [.. typePrefix, Rlp.EmptyByteArrayByte],
            };
            Span<byte> recovered = stackalloc byte[PublicKey.PrefixedLengthInBytes];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(TxDecoder.TryGetSignedPayload(encoded, txType, out _), Is.False);
                Assert.That(ecdsa.TryRecoverPublicKey(tx, encoded, recovered), Is.True);
                Assert.That(PublicKey.ComputeAddress(recovered[1..]), Is.EqualTo(key.Address));
            }
        }

        [Test]
        public void TryRecoverPublicKey_over_another_transactions_encoding_recovers_a_different_sender()
        {
            EthereumEcdsa ecdsa = new(TestBlockchainIds.ChainId);
            Transaction tx = Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject;
            Transaction other = Build.A.Transaction.WithNonce(2).SignedAndResolved().TestObject;
            Span<byte> recovered = stackalloc byte[PublicKey.PrefixedLengthInBytes];

            byte[] otherEncoded = TxDecoder.Instance.Encode(other, RlpBehaviors.SkipTypedWrapping).Bytes;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(ecdsa.TryRecoverPublicKey(tx, otherEncoded, recovered), Is.True);
                Assert.That(PublicKey.ComputeAddress(recovered[1..]), Is.Not.EqualTo(tx.SenderAddress));
            }
        }

        private static Transaction BuildSigned(TxType txType, PrivateKey key, bool eip155, int dataLength = 0)
        {
            TransactionBuilder<Transaction> builder = Build.A.Transaction.WithType(txType).WithChainId(TestBlockchainIds.ChainId);

            if (dataLength > 0) builder = builder.WithData(new byte[dataLength]);

            if (txType == TxType.Blob) builder = builder.WithBlobVersionedHashes(1).WithMaxFeePerBlobGas(1);
            if (txType == TxType.SetCode) builder = builder.WithAuthorizationCodeIfAuthorizationListTx();

            Transaction tx = builder.TestObject;
            new EthereumEcdsa(TestBlockchainIds.ChainId).Sign(key, tx, isEip155Enabled: eip155);
            return tx;
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

        // Typed txs are served from the sender cache on repeat recovery; legacy txs are not cached
        [TestCase(TxType.EIP1559, true)]
        [TestCase(TxType.Legacy, false)]
        public void RecoverAddress_repeat_recovery_uses_sender_cache_for_typed_tx_only(TxType txType, bool servedFromCache)
        {
            // The sender cache is a process-wide static; reset it so the miss/hit sequence
            // asserted below cannot depend on what other tests recovered earlier.
            EthereumEcdsaExtensions.ClearSenderCache();

            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            PrivateKey keyA = TestItem.PrivateKeyA;
            // Unique content per case so the process-wide cache cannot collide across tests
            static Transaction Create(TxType txType) => Build.A.Transaction
                .WithType(txType)
                .WithNonce(txType == TxType.Legacy ? 0xBEEFUL : 0xC0FFEEUL)
                .TestObject;

            Transaction txA = Create(txType);
            ecdsa.Sign(keyA, txA);
            txA.Hash = txA.CalculateHash();
            Assert.That(ecdsa.RecoverAddress(txA), Is.EqualTo(keyA.Address));

            // An ecdsa that recovers nothing: only a cache hit returns the previously recovered sender
            Assert.That(NonRecoveringEcdsa(ecdsa.ChainId).RecoverAddress(txA), Is.EqualTo(servedFromCache ? keyA.Address : null));
        }

        /// <summary>
        /// Every recovery of a typed transaction computes its signing hash, so a sender-cache hit must allocate nothing
        /// and a miss no more than the ECDSA recovery of the address itself.
        /// </summary>
        [Test]
        public void RecoverAddress_allocates_no_more_than_the_ecdsa_recovery()
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            Transaction warmUp = UniqueTypedTx(ecdsa, 0xA110C0UL);
            Transaction tx = UniqueTypedTx(ecdsa, 0xA110C1UL);
            KeccakRlpWriter writer = new();
            TxDecoder.Instance.EncodeTx(ref writer, tx, RlpBehaviors.SkipTypedWrapping, forSigning: true, isEip155Enabled: true, BlockchainIds.Sepolia);
            ValueHash256 signingHash = writer.GetValueHash();
            ecdsa.RecoverAddress(warmUp);
            ecdsa.RecoverAddress(warmUp);
            ecdsa.RecoverAddress(warmUp.Signature!, in signingHash);

            long start = GC.GetAllocatedBytesForCurrentThread();
            Address? recoveredDirectly = ecdsa.RecoverAddress(tx.Signature!, in signingHash);
            long ecdsaRecovery = GC.GetAllocatedBytesForCurrentThread() - start;

            start = GC.GetAllocatedBytesForCurrentThread();
            Address? missed = ecdsa.RecoverAddress(tx);
            long miss = GC.GetAllocatedBytesForCurrentThread() - start;

            start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                ecdsa.RecoverAddress(tx);
            }
            long hits = GC.GetAllocatedBytesForCurrentThread() - start;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(recoveredDirectly, Is.EqualTo(TestItem.PrivateKeyA.Address), "the signing hash under test");
                Assert.That(missed, Is.EqualTo(TestItem.PrivateKeyA.Address));
                Assert.That(miss, Is.EqualTo(ecdsaRecovery), "a miss");
                Assert.That(hits, Is.Zero, "cache hits");
            }
        }

        /// <summary>A signing hash left unfinished on this thread, as an exception while encoding would leave it, does not reach the next recovery.</summary>
        [Test]
        public void RecoverAddress_after_an_unfinished_signing_hash_recovers_the_signer()
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            ecdsa.RecoverAddress(UniqueTypedTx(ecdsa, 0xA110C2UL));
            KeccakHash hasher = (KeccakHash)typeof(EthereumEcdsaExtensions)
                .GetField("_signingHasher", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            hasher.Update(new byte[300]);

            Assert.That(ecdsa.RecoverAddress(UniqueTypedTx(ecdsa, 0xA110C3UL)), Is.EqualTo(TestItem.PrivateKeyA.Address));
        }

        // Unique content, which nothing else recovers, so the first recovery of each is a miss.
        private static Transaction UniqueTypedTx(IEthereumEcdsa ecdsa, ulong nonce) => Build.A.Transaction
            .WithType(TxType.EIP1559)
            .WithChainId(BlockchainIds.Sepolia)
            .WithNonce(nonce)
            .SignedAndResolved(ecdsa, TestItem.PrivateKeyA)
            .TestObject;

        public enum SignedField { Nonce, Signature, RecoveryId }

        /// <summary>Transactions that differ only in the nonce, the signature or the recovery id each recover their own signer.</summary>
        [Test]
        public void RecoverAddress_recovers_the_signer_of_each_signed_variant([Values] SignedField change)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            // Unique content per case, which nothing else recovers, so the process-wide cache holds no entry for it.
            Transaction original = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithChainId(BlockchainIds.Sepolia)
                .WithNonce(0xFACADEUL + (ulong)change)
                .SignedAndResolved(ecdsa, TestItem.PrivateKeyA)
                .TestObject;
            byte[] encoded = TxDecoder.Instance.Encode(original, RlpBehaviors.SkipTypedWrapping).Bytes;

            // Decoded, the hash comes from the raw bytes and stays when the transaction changes.
            Transaction changed = Rlp.Decode<Transaction>(encoded, RlpBehaviors.SkipTypedWrapping)!;
            switch (change)
            {
                case SignedField.Nonce:
                    changed.Nonce++;
                    break;
                case SignedField.Signature:
                    ecdsa.Sign(TestItem.PrivateKeyB, changed);
                    break;
                case SignedField.RecoveryId:
                    // Same signing hash, r and s: the other parity recovers another valid key.
                    Signature signature = changed.Signature!;
                    changed.Signature = new Signature(signature.RAsSpan, signature.SAsSpan, (ulong)(Signature.VOffset + 1 - signature.RecoveryId));
                    break;
            }
            Address? changedSender = ecdsa.RecoverAddress(changed);

            Transaction received = Rlp.Decode<Transaction>(encoded, RlpBehaviors.SkipTypedWrapping)!;
            Address? receivedSender = ecdsa.RecoverAddress(received);
            Address? changedSenderAgain = ecdsa.RecoverAddress(changed);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(changed.Hash, Is.EqualTo(received.Hash), "the changed transaction keeps the original's hash");
                Assert.That(changedSender, Is.Not.Null.And.Not.EqualTo(TestItem.PrivateKeyA.Address), "the changed transaction recovers to another sender");
                Assert.That(receivedSender, Is.EqualTo(TestItem.PrivateKeyA.Address), "the original, after the changed one");
                Assert.That(changedSenderAgain, Is.EqualTo(changedSender), "the changed transaction, after the original");
            }
        }

        private static IEthereumEcdsa NonRecoveringEcdsa(ulong chainId)
        {
            IEthereumEcdsa ecdsa = Substitute.For<IEthereumEcdsa>();
            ecdsa.ChainId.Returns(chainId);
            return ecdsa;
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
