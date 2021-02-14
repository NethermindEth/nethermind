//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
            yield return ("access list", Build.A.Transaction.WithChainId(1).WithType(TxType.AccessList).SignedAndResolved().TestObject);
        }

        [TestCaseSource(nameof(TestCaseSources))]
        public void Signature_verify_test((string Name, Transaction Tx) testCase)
        {
            EthereumEcdsa ecdsa = new EthereumEcdsa(ChainId.Ropsten, LimboLogs.Instance);
            ecdsa.Verify(testCase.Tx.SenderAddress!, testCase.Tx);
        }

        
        [TestCase(true)]
        [TestCase(false)]
        public void Signature_test_ropsten(bool eip155)
        {
            EthereumEcdsa ecdsa = new EthereumEcdsa(ChainId.Ropsten, LimboLogs.Instance);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.TestObject;
            ecdsa.Sign(key, tx, eip155);
            Address address = ecdsa.RecoverAddress(tx);
            Assert.AreEqual(key.Address, address);
        }
        
        [Test]
        public void Test_eip155_for_the_first_ropsten_transaction()
        {
            Transaction tx = Rlp.Decode<Transaction>(new Rlp(Bytes.FromHexString("0xf85f808082520894353535353535353535353535353535353535353580801ca08d24b906be2d91a0bf2168862726991cc408cddf94cb087b392ce992573be891a077964b4e55a5c8ec7b85087d619c641c06def33ab052331337ca9efcd6b82aef")));
            
            Assert.AreEqual(new Keccak("0x5fd225549ed5c587c843e04578bdd4240fc0d7ab61f8e9faa37e84ec8dc8766d"), tx.Hash, "hash");
            EthereumEcdsa ecdsa = new EthereumEcdsa(ChainId.Ropsten, LimboLogs.Instance);
            Address from = ecdsa.RecoverAddress(tx);
            Assert.AreEqual(new Address("0x874b54a8bd152966d63f706bae1ffeb0411921e5"), from, "from");
        }
        
        [TestCase(true)]
        [TestCase(false)]
        public void Signature_test_olympic(bool isEip155Enabled)
        {
            EthereumEcdsa ecdsa = new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.TestObject;
            ecdsa.Sign(key, tx, isEip155Enabled);
            Address address = ecdsa.RecoverAddress(tx);
            Assert.AreEqual(key.Address, address);
        }
        
        [Test]
        public void Sign_goerli()
        {
            EthereumEcdsa ecdsa = new EthereumEcdsa(ChainId.Goerli, LimboLogs.Instance);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.TestObject;
            ecdsa.Sign(key, tx, true);
            Address address = ecdsa.RecoverAddress(tx);
            Assert.AreEqual(key.Address, address);
        }
        
        [Test]
        public void Recover_kovan([Values(false, true)] bool eip155)
        {
            EthereumEcdsa singEcdsa = new EthereumEcdsa(ChainId.Mainnet, LimboLogs.Instance);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.TestObject;
            singEcdsa.Sign(key, tx, eip155);
            
            EthereumEcdsa recoverEcdsa = new EthereumEcdsa(ChainId.Kovan, LimboLogs.Instance);
            Address address = recoverEcdsa.RecoverAddress(tx, true);
            Assert.AreEqual(key.Address, address);
        }
    }
}
