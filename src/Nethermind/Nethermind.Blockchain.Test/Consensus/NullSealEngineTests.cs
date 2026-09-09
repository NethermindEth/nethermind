// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Consensus
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class NullSealEngineTests
    {
        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Default_hints()
        {
            ISealValidator sealValidator = NullSealEngine.Instance;
            sealValidator.HintValidationRange(Guid.Empty, 0, 0);
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Test()
        {
            NullSealEngine engine = NullSealEngine.Instance;
            Assert.That(engine.Address, Is.EqualTo(Address.Zero));
            Assert.That(engine.CanSeal(0, null), Is.True);
            Assert.That(engine.ValidateParams(null, null), Is.True);
            Assert.That(engine.ValidateSeal(null, true), Is.True);
            Assert.That(engine.ValidateSeal(null, false), Is.True);
            Block block = Build.A.Block.TestObject;
            Assert.That(engine.SealBlock(block, CancellationToken.None).Result, Is.EqualTo(block));
        }
    }
}
