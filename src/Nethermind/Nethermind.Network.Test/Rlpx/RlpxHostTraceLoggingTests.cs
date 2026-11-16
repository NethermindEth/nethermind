// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Common.Internal.Logging;
using DotNetty.Handlers.Logging;
using DotNetty.Transport.Channels.Embedded;
using Microsoft.Extensions.Logging;
using Nethermind.Logging;
using Nethermind.Logging.Microsoft;
using Nethermind.Network.Rlpx;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx;

// Replaces the process-wide DotNetty logger factory, so no other test may run at the same time.
[NonParallelizable]
public class RlpxHostTraceLoggingTests
{
    private ILoggerFactory _originalFactory = null!;

    [SetUp]
    public void SetUp()
    {
        _originalFactory = InternalLoggerFactory.DefaultFactory;
        // DotNetty types cache their logger in static fields on first use. Touch them before the swap,
        // so they keep the original factory after this fixture.
        new EmbeddedChannel().FinishAndReleaseAll();
    }

    [TearDown]
    public void TearDown() => InternalLoggerFactory.DefaultFactory = _originalFactory;

    [TestCase(Nethermind.Logging.LogLevel.Trace, true)]
    [TestCase(Nethermind.Logging.LogLevel.Debug, false)]
    [TestCase(Nethermind.Logging.LogLevel.Info, false)]
    public void Logging_handler_is_added_only_when_trace_is_enabled(Nethermind.Logging.LogLevel level, bool expected)
    {
        InternalLoggerFactory.DefaultFactory = new NethermindLoggerFactory(new TestLogManager(level), lowerLogLevel: true);
        EmbeddedChannel channel = new();

        RlpxHost.AddTraceLoggingHandler(channel.Pipeline, "OUT");

        Assert.That(channel.Pipeline.Get<LoggingHandler>() is not null, Is.EqualTo(expected));
        channel.FinishAndReleaseAll();
    }
}
