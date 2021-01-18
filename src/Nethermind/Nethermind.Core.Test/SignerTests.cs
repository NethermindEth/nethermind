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

using System;
using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class EcdsaTEsts
    {
        [OneTimeSetUp]
        public void SetUp()
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
        }

        [TestCase("0x9242685bf161793cc25603c231bc2f568eb630ea16aa137d2664ac80388256084f8ae3bd7535248d0bd448298cc2e2071e56992d0774dc340c368ae950852ada1c")]
        [TestCase("0x34ff4b97a0ec8f735f781f250dcd3070a72ddb640072dd39553407d0320db79939e3b080ecaa2e9f248214c6f0811fb4b4ba05b7bcff254c053e47d8513e820900")]
        public void Hex_and_back_again(string hexSignature)
        {
            Signature signature = new Signature(hexSignature);
            string hexAgain = signature.ToString();
            Assert.AreEqual(hexSignature, hexAgain);
        }

        [Test]
        public void Sign_and_recover()
        {
            EthereumEcdsa ethereumEcdsa = new EthereumEcdsa(ChainId.Olympic, LimboLogs.Instance);

            Keccak message = Keccak.Compute("Test message");
            PrivateKey privateKey = Build.A.PrivateKey.TestObject;
            Signature signature = ethereumEcdsa.Sign(privateKey, message);
            Assert.AreEqual(privateKey.Address, ethereumEcdsa.RecoverAddress(signature, message));
        }
    }
}
