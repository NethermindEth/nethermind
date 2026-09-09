// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Consensus
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class NullSignerTests
    {
        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Test()
        {
            NullSigner signer = NullSigner.Instance;
            Assert.That(signer.Address, Is.EqualTo(Address.Zero));
            Assert.That(signer.CanSign, Is.False);
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Test_signing()
        {
            NullSigner signer = NullSigner.Instance;
            Assert.That(signer.TrySign((Transaction)null!), Is.False, "null signer cannot sign");
            ValueHash256 hash = default;
            Assert.That(signer.TrySign(in hash, out Signature signature), Is.False, "null signer cannot sign a hash");
            Assert.That(signature, Is.Null);
        }
    }
}
