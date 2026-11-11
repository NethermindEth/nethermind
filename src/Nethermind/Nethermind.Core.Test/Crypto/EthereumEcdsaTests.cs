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
        [Test]
        public void Verified_recovery_lookup_checks_every_input([Values(1, 64, 65, 100)] int count)
        {
            byte[] messages = new byte[count * 32];
            byte[] signatures = new byte[count * 64];
            byte[] ids = new byte[count];
            byte[] keys = new byte[count * 64];
            Random random = new(42);
            random.NextBytes(messages);
            random.NextBytes(signatures);
            random.NextBytes(keys);
            VerifiedRecoveries recoveries = new(messages, signatures, ids, keys);
            Span<byte> actual = stackalloc byte[64];
            for (int i = count - 1; i >= 0; i--)
            {
                byte[] message = messages.AsSpan(i * 32, 32).ToArray();
                byte[] signature = signatures.AsSpan(i * 64, 64).ToArray();
                bool found = recoveries.TryGetKey(signature, 0, message, actual);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(found, Is.True);
                    Assert.That(actual.ToArray(), Is.EqualTo(keys.AsSpan(i * 64, 64).ToArray()));
                    Assert.That(recoveries.TryGetKey(signature, 1, message, actual), Is.False);
                }
                message[0] ^= 1;
                Assert.That(recoveries.TryGetKey(signature, 0, message, actual), Is.False);
                message[0] ^= 1;
                signature[0] ^= 1;
                Assert.That(recoveries.TryGetKey(signature, 0, message, actual), Is.False);
                signature[0] ^= 1;
                signature[63] ^= 1;
                Assert.That(recoveries.TryGetKey(signature, 0, message, actual), Is.False);
            }
        }

        public static IEnumerable<(string, Transaction)> TestCaseSources()
        {
            yield return ("legacy", Build.A.Transaction.SignedAndResolved().TestObject);
            yield return ("access list", Build.A.Transaction.WithChainId(TestBlockchainIds.ChainId).WithType(TxType.AccessList).SignedAndResolved().TestObject);
        }

        /// <summary>On these chains 2 * chainId + 35 or + 36 wraps around a ulong onto 27 or 28.</summary>
        [TestCase((1ul << 63) - 4)]
        [TestCase(ulong.MaxValue - 3)]
        public void Pre_eip155_legacy_signature_recovers_without_chain_id_on_large_chain_ids(ulong chainId)
        {
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.WithType(TxType.Legacy).WithChainId(null).TestObject;
            EthereumEcdsa ecdsa = new(chainId);
            ecdsa.Sign(key, tx, isEip155Enabled: false);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(tx.Signature!.V, Is.EqualTo(27).Or.EqualTo(28));
                Assert.That(ecdsa.RecoverAddress(tx), Is.EqualTo(key.Address));
            }
        }

        [TestCase(EthereumEcdsaExtensions.MaxEip155ChainId, true)]
        [TestCase(EthereumEcdsaExtensions.MaxEip155ChainId + 2, false)]
        [TestCase(1ul << 63, false)]
        public void Eip155_legacy_signature_round_trips_or_fails_on_large_chain_ids(ulong chainId, bool representable)
        {
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.WithType(TxType.Legacy).WithChainId(null).TestObject;
            EthereumEcdsa ecdsa = new(chainId);

            if (!representable)
            {
                Assert.That(() => ecdsa.Sign(key, tx), Throws.TypeOf<OverflowException>());
                return;
            }

            ecdsa.Sign(key, tx);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(tx.Signature!.ChainId, Is.EqualTo(chainId));
                Assert.That(ecdsa.RecoverAddress(tx), Is.EqualTo(key.Address));
            }
        }

        [TestCase(EthereumEcdsaExtensions.MaxEip155ChainId, true, ulong.MaxValue - 1)]
        [TestCase(EthereumEcdsaExtensions.MaxEip155ChainId, false, ulong.MaxValue - 2)]
        [TestCase((ulong.MaxValue - 35) / 2, false, ulong.MaxValue)]
        public void CalculateV_at_the_largest_chain_ids(ulong chainId, bool addParity, ulong expected)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(EthereumEcdsaExtensions.CalculateV(chainId, addParity), Is.EqualTo(expected));
                Assert.That(EthereumEcdsaExtensions.TryCalculateV(chainId, addParity, out ulong v), Is.True);
                Assert.That(v, Is.EqualTo(expected));
            }
        }

        [TestCase((ulong.MaxValue - 35) / 2, true)]
        [TestCase((ulong.MaxValue - 35) / 2 + 1, false)]
        [TestCase(ulong.MaxValue, false)]
        public void CalculateV_throws_where_no_v_fits(ulong chainId, bool addParity)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(() => EthereumEcdsaExtensions.CalculateV(chainId, addParity), Throws.TypeOf<OverflowException>());
                Assert.That(EthereumEcdsaExtensions.TryCalculateV(chainId, addParity, out _), Is.False);
            }
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
                Assert.That(ecdsa.RecoverAddress(tx, encoded, useSignatureChainId), Is.EqualTo(key.Address));
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
            Address? recovered = ecdsa.RecoverAddress(txA);
            // An ecdsa that recovers nothing: only a cache hit returns the previously recovered sender
            Address? cached = NonRecoveringEcdsa(ecdsa.ChainId).RecoverAddress(txA);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(recovered, Is.EqualTo(keyA.Address));
                Assert.That(cached, Is.EqualTo(servedFromCache ? keyA.Address : null));
            }
        }

        [Test]
        public void RecoverAddress_from_the_transaction_and_from_its_encoding_share_the_sender_cache([Values] bool encodingFirst)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            Transaction tx = UniqueTypedTx(ecdsa, encodingFirst ? 0xA110C4UL : 0xA110C5UL);
            byte[] encoded = Encode(tx);
            IEthereumEcdsa nonRecovering = NonRecoveringEcdsa(ecdsa.ChainId);

            Address? recovered = encodingFirst ? ecdsa.RecoverAddress(tx, encoded) : ecdsa.RecoverAddress(tx);
            Address? cached = encodingFirst ? nonRecovering.RecoverAddress(tx) : nonRecovering.RecoverAddress(tx, encoded);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(recovered, Is.EqualTo(TestItem.PrivateKeyA.Address));
                Assert.That(cached, Is.EqualTo(TestItem.PrivateKeyA.Address));
            }
        }

        /// <summary>
        /// Every recovery of a typed transaction computes its signing hash, so a sender-cache hit must allocate nothing
        /// and a miss no more than the ECDSA recovery of the address itself.
        /// </summary>
        [Test]
        public void RecoverAddress_allocates_no_more_than_the_ecdsa_recovery([Values] bool fromEncoding)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            Transaction warmUp = UniqueTypedTx(ecdsa, fromEncoding ? 0xA110C6UL : 0xA110C0UL);
            Transaction tx = UniqueTypedTx(ecdsa, fromEncoding ? 0xA110C7UL : 0xA110C1UL);
            byte[] warmUpEncoded = Encode(warmUp);
            byte[] encoded = Encode(tx);
            KeccakRlpWriter writer = new();
            TxDecoder.Instance.EncodeTx(ref writer, tx, RlpBehaviors.SkipTypedWrapping, forSigning: true, isEip155Enabled: true, BlockchainIds.Sepolia);
            ValueHash256 signingHash = writer.GetValueHash();
            Recover(warmUp, warmUpEncoded);
            Recover(warmUp, warmUpEncoded);
            ecdsa.RecoverAddress(warmUp.Signature!, in signingHash);

            long start = GC.GetAllocatedBytesForCurrentThread();
            Address? recoveredDirectly = ecdsa.RecoverAddress(tx.Signature!, in signingHash);
            long ecdsaRecovery = GC.GetAllocatedBytesForCurrentThread() - start;

            start = GC.GetAllocatedBytesForCurrentThread();
            Address? missed = Recover(tx, encoded);
            long miss = GC.GetAllocatedBytesForCurrentThread() - start;

            start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                Recover(tx, encoded);
            }
            long hits = GC.GetAllocatedBytesForCurrentThread() - start;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(recoveredDirectly, Is.EqualTo(TestItem.PrivateKeyA.Address), "the signing hash under test");
                Assert.That(missed, Is.EqualTo(TestItem.PrivateKeyA.Address));
                Assert.That(miss, Is.EqualTo(ecdsaRecovery), "a miss");
                Assert.That(hits, Is.Zero, "cache hits");
            }

            Address? Recover(Transaction transaction, byte[] transactionEncoding) =>
                fromEncoding ? ecdsa.RecoverAddress(transaction, transactionEncoding) : ecdsa.RecoverAddress(transaction);
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

        private static byte[] Encode(Transaction tx) => TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;

        public enum SignedField { Nonce, Signature, RecoveryId }

        /// <summary>Transactions that differ only in the nonce, the signature or the recovery id each recover their own signer.</summary>
        /// <remarks>With <paramref name="fromEncoding"/>, each transaction is recovered from its own encoding.</remarks>
        [Test]
        public void RecoverAddress_recovers_the_signer_of_each_signed_variant([Values] SignedField field, [Values] bool fromEncoding)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia);
            // Unique content per case, which nothing else recovers, so the process-wide cache holds no entry for it.
            Transaction signed = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithChainId(BlockchainIds.Sepolia)
                .WithNonce(0xFACADEUL + 4 * (ulong)field + (fromEncoding ? 2UL : 0UL))
                .SignedAndResolved(ecdsa, TestItem.PrivateKeyA)
                .TestObject;
            byte[] encoded = Encode(signed);
            Transaction original = Rlp.Decode<Transaction>(encoded, RlpBehaviors.SkipTypedWrapping)!;

            // The cache key does not include Hash.
            Transaction variant = Rlp.Decode<Transaction>(encoded, RlpBehaviors.SkipTypedWrapping)!;
            switch (field)
            {
                case SignedField.Nonce:
                    variant.Nonce++;
                    break;
                case SignedField.Signature:
                    ecdsa.Sign(TestItem.PrivateKeyB, variant);
                    break;
                case SignedField.RecoveryId:
                    // Same signing hash, r and s: the other parity recovers another valid key.
                    Signature signature = variant.Signature!;
                    variant.Signature = new Signature(signature.RAsSpan, signature.SAsSpan, (ulong)(Signature.VOffset + 1 - signature.RecoveryId));
                    break;
            }

            Address? variantSender = Recover(variant);
            Address? originalSender = Recover(original);
            Address? variantSenderAgain = Recover(variant);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(variant.Hash, Is.EqualTo(original.Hash), "precondition: equal Hash");
                Assert.That(variantSender, Is.Not.Null.And.Not.EqualTo(TestItem.PrivateKeyA.Address), "the variant recovers another signer");
                Assert.That(originalSender, Is.EqualTo(TestItem.PrivateKeyA.Address), "the original recovers its signer");
                Assert.That(variantSenderAgain, Is.EqualTo(variantSender), "the variant recovers the same signer again");
            }

            Address? Recover(Transaction tx) => fromEncoding ? ecdsa.RecoverAddress(tx, Encode(tx)) : ecdsa.RecoverAddress(tx);
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
