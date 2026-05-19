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
    [Parallelizable(ParallelScope.All)]
    public class SignerTests
    {
        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Address_is_zero_when_key_is_null()
        {
            // not a great fan of using Address.Zero like a null value but let us show in test
            // what it does
            Signer signer = new(1, (PrivateKey?)null, LimboLogs.Instance);
            signer.Address.Should().Be(Address.Zero);
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Cannot_sign_when_null_key()
        {
            Signer signer = new(1, (PrivateKey?)null, LimboLogs.Instance);
            signer.CanSign.Should().BeFalse();
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Can_set_signer_to_null()
        {
            Signer signer = new(1, TestItem.PrivateKeyA, LimboLogs.Instance);
            signer.CanSign.Should().BeTrue();
            signer.SetSigner((PrivateKey?)null);
            signer.CanSign.Should().BeFalse();
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Can_set_signer_to_protected_null()
        {
            Signer signer = new(1, TestItem.PrivateKeyA, LimboLogs.Instance);
            signer.CanSign.Should().BeTrue();
            signer.SetSigner((ProtectedPrivateKey?)null);
            signer.CanSign.Should().BeFalse();
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void TrySign_returns_false_when_key_is_null()
        {
            Signer signer = new(1, (PrivateKey?)null, LimboLogs.Instance);
            ValueHash256 hash = Keccak.Zero;
            signer.TrySign(in hash, out Signature signature).Should().BeFalse("signer with no key cannot sign");
            signature.Should().BeNull();
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Test_signing()
        {
            Signer signer = new(1, TestItem.PrivateKeyA, LimboLogs.Instance);
            signer.TrySign(Build.A.Transaction.TestObject).Should().BeTrue("signer with key signs a transaction");
            ValueHash256 hash = Keccak.Zero;
            signer.TrySign(in hash, out Signature signature).Should().BeTrue("signer with key signs a hash");
            signature.Bytes.Length.Should().Be(64);
        }
    }
}
