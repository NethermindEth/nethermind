// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Logging.NLog.Test
{
    [TestFixture]
    public class NullLoggerTests
    {
        [Test]
        public void Test()
        {
            NullLogger nullLogger = NullLogger.Instance;
            nullLogger.IsDebug.Should().BeFalse();
            nullLogger.IsInfo.Should().BeFalse();
            nullLogger.IsWarn.Should().BeFalse();
            nullLogger.IsError.Should().BeFalse();
            nullLogger.IsTrace.Should().BeFalse();

            nullLogger.Debug(null);
            nullLogger.Info(null);
            nullLogger.Warn(null);
            nullLogger.Error(null);
            nullLogger.Trace(null);
        }
    }
}
