// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Consensus;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Consensus
{
    [TestFixture]
    public class SealEngineExceptionTests
    {
        [Test, Timeout(Timeout.MaxTestTime)]
        public void Test()
        {
            SealEngineException exception = new("message");
            exception.Message.Should().Be("message");
        }
    }
}
