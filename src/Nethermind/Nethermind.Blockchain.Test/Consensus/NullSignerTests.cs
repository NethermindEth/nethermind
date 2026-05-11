// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using NUnit.Framework;
// ReSharper disable AssignNullToNotNullAttribute

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
        public async Task Test_signing()
        {
            NullSigner signer = NullSigner.Instance;
            await signer.Sign((Transaction)null!);
            Assert.That(signer.Sign((Hash256)null!).Bytes.Length, Is.EqualTo(64));
        }
    }
}
