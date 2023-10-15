// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        [Test, Timeout(Timeout.MaxTestTime)]
        public void Address_is_zero_when_key_is_null()
        {
            // not a great fan of using Address.Zero like a null value but let us show in test
            // what it does
            Signer signer = new(1, (PrivateKey?)null, LimboLogs.Instance);
            signer.Address.Should().Be(Address.Zero);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Cannot_sign_when_null_key()
        {
            Signer signer = new(1, (PrivateKey?)null, LimboLogs.Instance);
            signer.CanSign.Should().BeFalse();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_set_signer_to_null()
        {
            Signer signer = new(1, TestItem.PrivateKeyA, LimboLogs.Instance);
            signer.CanSign.Should().BeTrue();
            signer.SetSigner((PrivateKey?)null);
            signer.CanSign.Should().BeFalse();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_set_signer_to_protected_null()
        {
            Signer signer = new(1, TestItem.PrivateKeyA, LimboLogs.Instance);
            signer.CanSign.Should().BeTrue();
            signer.SetSigner((ProtectedPrivateKey?)null);
            signer.CanSign.Should().BeFalse();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Throws_when_trying_to_sign_with_a_null_key()
        {
            Signer signer = new(1, (PrivateKey?)null, LimboLogs.Instance);
            Assert.Throws<InvalidOperationException>(() => signer.Sign(Keccak.Zero));
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public async Task Test_signing()
        {
            Signer signer = new(1, TestItem.PrivateKeyA, LimboLogs.Instance);
            await signer.Sign(Build.A.Transaction.TestObject);
            signer.Sign(Keccak.Zero).Bytes.Should().HaveCount(64);
        }
    }
}
