// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
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
            signer.Address.Should().Be(Address.Zero);
            signer.CanSign.Should().BeFalse();
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Test_signing()
        {
            NullSigner signer = NullSigner.Instance;
            signer.TrySign((Transaction)null!).Should().BeFalse("null signer cannot sign");
            ValueHash256 hash = default;
            signer.TrySign(in hash, out Signature signature).Should().BeFalse("null signer cannot sign a hash");
            signature.Should().BeNull();
        }
    }
}
