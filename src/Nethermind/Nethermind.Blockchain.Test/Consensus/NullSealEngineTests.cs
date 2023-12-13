// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Consensus
{
    [TestFixture]
    public class NullSealEngineTests
    {
        [Test, Timeout(Timeout.MaxTestTime)]
        public void Default_hints()
        {
            ISealValidator sealValidator = NullSealEngine.Instance;
            sealValidator.HintValidationRange(Guid.Empty, 0, 0);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Test()
        {
            NullSealEngine engine = NullSealEngine.Instance;
            engine.Address.Should().Be(Address.Zero);
            engine.CanSeal(0, null).Should().BeTrue();
            engine.ValidateParams(null, null).Should().BeTrue();
            engine.ValidateSeal(null, true).Should().BeTrue();
            engine.ValidateSeal(null, false).Should().BeTrue();
            engine.SealBlock(null, CancellationToken.None).Result.Should().Be(null);
        }
    }
}
