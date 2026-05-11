// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Consensus
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class SealEngineExceptionTests
    {
        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Test()
        {
            SealEngineException exception = new("message");
            Assert.That(exception.Message, Is.EqualTo("message"));
        }
    }
}
