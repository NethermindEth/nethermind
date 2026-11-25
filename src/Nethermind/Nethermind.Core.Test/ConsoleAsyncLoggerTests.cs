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
        ConsoleAsyncLogger logger = new(LogLevel.Info);
        try
        {
            logger.Info("first");
            Assert.That(error.WriteStarted.Wait(TimeSpan.FromSeconds(10)), Is.True);
            logger.Info("second");
            logger.Info("third");

            Task flush = Task.Run(logger.Flush);
            try
            {
                Assert.That(() => logger.Info("flush probe"), Throws.InvalidOperationException.After(10_000, 10));
            }
            finally
            {
                error.Release.Set();
                Assert.That(flush.Wait(TimeSpan.FromSeconds(10)), Is.True);
            }

            Assert.That(error.ToString(), Does.Contain("first").And.Contain("second").And.Contain("third"));
        }
        finally
        {
            error.Release.Set();
            try
            {
                logger.Flush();
            }
            finally
            {
                Console.SetError(originalError);
            }
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
