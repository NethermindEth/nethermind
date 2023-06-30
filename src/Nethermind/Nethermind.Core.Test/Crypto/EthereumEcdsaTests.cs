// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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

        [Test]
        public void Test_eip155_for_the_first_ropsten_transaction()
        {
            Transaction tx = Rlp.Decode<Transaction>(new Rlp(Bytes.FromHexString("0xf85f808082520894353535353535353535353535353535353535353580801ca08d24b906be2d91a0bf2168862726991cc408cddf94cb087b392ce992573be891a077964b4e55a5c8ec7b85087d619c641c06def33ab052331337ca9efcd6b82aef")));

            Assert.That(tx.Hash, Is.EqualTo(new Keccak("0x5fd225549ed5c587c843e04578bdd4240fc0d7ab61f8e9faa37e84ec8dc8766d")), "hash");
            EthereumEcdsa ecdsa = new(BlockchainIds.Sepolia, LimboLogs.Instance);
            Address? from = ecdsa.RecoverAddress(tx);
            Assert.That(from, Is.EqualTo(new Address("0x874b54a8bd152966d63f706bae1ffeb0411921e5")), "from");
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
