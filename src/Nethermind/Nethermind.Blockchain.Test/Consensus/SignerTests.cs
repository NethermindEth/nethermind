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
// 

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using NUnit.Framework;
// ReSharper disable AssignNullToNotNullAttribute

namespace Nethermind.Blockchain.Test.Consensus
{
    [TestFixture]
    public class SignerTests
    {
        [Test]
        public void Throws_when_null_log_manager_in_constructor()
        {
            Assert.Throws<ArgumentNullException>(() => new Signer(1, (PrivateKey) null, null));
            Assert.Throws<ArgumentNullException>(() => new Signer(1, (ProtectedPrivateKey) null, null));
        }

        [Test]
        public void Address_is_zero_when_key_is_null()
        {
            // not a great fan of using Address.Zero like a null value but let us show in test
            // what it does
            Signer signer = new Signer(1, (PrivateKey)null, LimboLogs.Instance);
            signer.Address.Should().Be(Address.Zero);
        }

        [Test]
        public void Cannot_sign_when_null_key()
        {
            Signer signer = new Signer(1, (PrivateKey)null, LimboLogs.Instance);
            signer.CanSign.Should().BeFalse();
        }
        
        [Test]
        public void Can_set_signer_to_null()
        {
            Signer signer = new Signer(1, TestItem.PrivateKeyA, LimboLogs.Instance);
            signer.CanSign.Should().BeTrue();
            signer.SetSigner((PrivateKey)null);
            signer.CanSign.Should().BeFalse();
        }
        
        [Test]
        public void Can_set_signer_to_protected_null()
        {
            Signer signer = new Signer(1, TestItem.PrivateKeyA, LimboLogs.Instance);
            signer.CanSign.Should().BeTrue();
            signer.SetSigner((ProtectedPrivateKey)null);
            signer.CanSign.Should().BeFalse();
        }
        
        [Test]
        public void Throws_when_trying_to_sign_with_a_null_key()
        {
            Signer signer = new Signer(1, (PrivateKey)null, LimboLogs.Instance);
            Assert.Throws<InvalidOperationException>(() => signer.Sign(Keccak.Zero));
        }
        
        [Test]
        public async Task Test_signing()
        {
            Signer signer = new Signer(1, TestItem.PrivateKeyA, LimboLogs.Instance);
            await signer.Sign(Build.A.Transaction.TestObject);
            signer.Sign(Keccak.Zero).Bytes.Should().HaveCount(64);
        }
    }
}
