// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Core.Test;

[NonParallelizable] // swaps the process-wide Console.Error
public class ConsoleAsyncLoggerTests
{
    [Test]
    public void Flush_writes_entries_queued_while_consumer_is_busy()
    {
        TextWriter originalError = Console.Error;
        using GatedWriter error = new();
        Console.SetError(error);
        try
        {
            ConsoleAsyncLogger logger = new(LogLevel.Info);
            logger.Info("first");
            Assert.That(error.WriteStarted.Wait(TimeSpan.FromSeconds(10)), Is.True);
            logger.Info("second");
            logger.Info("third");

            // Holds the consumer in its first write until Flush has completed adding.
            Task release = Task.Delay(TimeSpan.FromMilliseconds(100)).ContinueWith(_ => error.Release.Set());
            logger.Flush();
            release.Wait();

            Assert.That(error.ToString(), Does.Contain("first").And.Contain("second").And.Contain("third"));
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    private sealed class GatedWriter : StringWriter
    {
        public ManualResetEventSlim WriteStarted { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public override void WriteLine(string? value)
        {
            WriteStarted.Set();
            Release.Wait();
            base.WriteLine(value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                WriteStarted.Dispose();
                Release.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
