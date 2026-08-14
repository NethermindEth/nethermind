// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    [Parallelizable(ParallelScope.All)]
    public class SignerTests
    {
        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Address_is_zero_when_key_is_null()
        {
            // not a great fan of using Address.Zero like a null value but let us show in test
            // what it does
            Signer signer = new(1, (PrivateKey?)null, LimboLogs.Instance);
            Assert.That(signer.Address, Is.EqualTo(Address.Zero));
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Cannot_sign_when_null_key()
        {
            Signer signer = new(1, (PrivateKey?)null, LimboLogs.Instance);
            Assert.That(signer.CanSign, Is.False);
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Can_set_signer_to_null()
        {
            Signer signer = new(1, TestItem.PrivateKeyA, LimboLogs.Instance);
            Assert.That(signer.CanSign, Is.True);
            signer.SetSigner((PrivateKey?)null);
            Assert.That(signer.CanSign, Is.False);
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Can_set_signer_to_protected_null()
        {
            Signer signer = new(1, TestItem.PrivateKeyA, LimboLogs.Instance);
            Assert.That(signer.CanSign, Is.True);
            signer.SetSigner((ProtectedPrivateKey?)null);
            Assert.That(signer.CanSign, Is.False);
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void TrySign_returns_false_when_key_is_null()
        {
            Signer signer = new(1, (PrivateKey?)null, LimboLogs.Instance);
            ValueHash256 hash = Keccak.Zero;
            Assert.That(signer.TrySign(in hash, out Signature signature), Is.False, "signer with no key cannot sign");
            Assert.That(signature, Is.Null);
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Test_signing()
        {
            Signer signer = new(1, TestItem.PrivateKeyA, LimboLogs.Instance);
            Assert.That(signer.TrySign(Build.A.Transaction.TestObject), Is.True, "signer with key signs a transaction");
            ValueHash256 hash = Keccak.Zero;
            Assert.That(signer.TrySign(in hash, out Signature signature), Is.True, "signer with key signs a hash");
            Assert.That(signature.Bytes.Length, Is.EqualTo(64));
        }
    }
}
