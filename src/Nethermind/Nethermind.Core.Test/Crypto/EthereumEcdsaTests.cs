// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
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

        public static IEnumerable<AuthorizationTuple> AuthorityTupleTestCaseSources()
        {
            yield return CreateAuthorizationTuple(ulong.MaxValue, Build.A.Address.TestObjectInternal, UInt256.MaxValue);
            yield return CreateAuthorizationTuple(1, Address.Zero, null);
        }

        [TestCaseSource(nameof(AuthorityTupleTestCaseSources))]
        public void RecoverAddress_AuthorizationTupleOfDifferentSize_RecoversAddressCorrectly(AuthorizationTuple authorization)
        {
            EthereumEcdsa ecdsa = new(BlockchainIds.GenericNonRealNetwork);

            Address? authority = ecdsa.RecoverAddress(authorization);

            Assert.That(authority, Is.EqualTo(authorization.Authority));
        }

        private static AuthorizationTuple CreateAuthorizationTuple(ulong chainId, Address codeAddress, UInt256? nonce)
        {
            AuthorizationTupleDecoder decoder = new();
            RlpStream rlp = decoder.EncodeWithoutSignature(chainId, codeAddress, nonce);
            Span<byte> code = stackalloc byte[rlp.Length + 1];
            code[0] = Eip7702Constants.Magic;
            rlp.Data.AsSpan().CopyTo(code.Slice(1));
            EthereumEcdsa ecdsa = new(1);
            PrivateKey signer = Build.A.PrivateKey.TestObject;
            Signature sig = ecdsa.Sign(signer, Keccak.Compute(code));

            return new AuthorizationTuple(chainId, codeAddress, nonce, sig, signer.Address);
        }
    }
}
