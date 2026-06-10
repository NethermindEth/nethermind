// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Logging.NLog.Test
{
    [TestFixture]
    public class NullLoggerTests
    {
        [Test]
        public void Test()
        {
            ILogger nullLogger = NullLogger.Instance;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(nullLogger.IsDebug, Is.False);
                Assert.That(nullLogger.IsInfo, Is.False);
                Assert.That(nullLogger.IsWarn, Is.False);
                Assert.That(nullLogger.IsError, Is.False);
                Assert.That(nullLogger.IsTrace, Is.False);
            }

            nullLogger.Debug(null);
            nullLogger.Info(null);
            nullLogger.Warn(null);
            nullLogger.Error(null);
            nullLogger.Trace(null);
        }
    }
}
