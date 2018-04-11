/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test.Crypto
{
    [TestFixture]
    public class EthereumSignerTests
    {   
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(1000000)]
        [TestCase(1700000)]
        [TestCase(2000000)]
        public void Signature_test_ropsten(int blockNumber)
        {
            EthereumSigner signer = new EthereumSigner(RopstenSpecProvider.Instance, NullLogger.Instance);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.TestObject;
            signer.Sign(key, tx, blockNumber);
            Address address = signer.RecoverAddress(tx, blockNumber);
            Assert.AreEqual(key.Address, address);
        }
        
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(1000000)]
        [TestCase(1700000)]
        [TestCase(2000000)]
        public void Signature_test_olympic(int blockNumber)
        {
            EthereumSigner signer = new EthereumSigner(OlympicSpecProvider.Instance, NullLogger.Instance);
            PrivateKey key = Build.A.PrivateKey.TestObject;
            Transaction tx = Build.A.Transaction.TestObject;
            signer.Sign(key, tx, blockNumber);
            Address address = signer.RecoverAddress(tx, blockNumber);
            Assert.AreEqual(key.Address, address);
        }
    }
}