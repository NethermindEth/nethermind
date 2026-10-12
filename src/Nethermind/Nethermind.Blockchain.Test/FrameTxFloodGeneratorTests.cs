// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Test.Builders;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

/// <summary>
/// Lifetime contract of the flood measurement's submitting thread, which the measurement itself cannot cover
/// because it is explicit and runs for minutes.
/// </summary>
[TestFixture]
public class FrameTxFloodGeneratorTests
{
    private const int OfferedRate = 1_000;

    private static readonly TimeSpan SubmissionWait = TimeSpan.FromSeconds(10);

    private static FrameTxFloodMeasurement.FloodGenerator Generator() =>
        new(static _ => AcceptTxResult.Accepted, [Build.A.Transaction.TestObject], OfferedRate);

    [Test]
    public void Run_stops_the_generator_when_the_body_throws()
    {
        using FrameTxFloodMeasurement.FloodGenerator generator = Generator();

        Assert.That(() => generator.Run<object>(static () => throw new InvalidOperationException("warmup")),
            Throws.TypeOf<InvalidOperationException>());
        Assert.That(generator.IsRunning, Is.False,
            "a failed warmup left the generator submitting into infrastructure the caller then tears down");
    }

    [Test]
    public void Run_stops_the_generator_when_the_body_completes()
    {
        using FrameTxFloodMeasurement.FloodGenerator generator = Generator();

        Assert.That(generator.Run(static () => true), Is.True);
        Assert.That(generator.IsRunning, Is.False);
    }

    [Test]
    public void Run_tolerates_the_body_stopping_the_generator_itself()
    {
        using FrameTxFloodMeasurement.FloodGenerator generator = Generator();

        Assert.That(generator.Run(() => generator.Stop()), Is.True,
            "the counters are read after this join, so a stop the body asks for must be reported honestly");
        Assert.That(generator.IsRunning, Is.False);
    }

    [Test]
    public void Run_submits_while_the_body_runs()
    {
        using FrameTxFloodMeasurement.FloodGenerator generator = Generator();

        Assert.That(
            generator.Run(() => SpinWait.SpinUntil(() => Volatile.Read(ref generator.Submitted) > 0, SubmissionWait)),
            Is.True,
            "the body ran without the generator offering a single transaction, so the arm measures an unflooded pool");
    }

    [Test]
    public void Run_leaves_disposal_to_the_owner()
    {
        using FrameTxFloodMeasurement.FloodGenerator generator = Generator();

        generator.Run(static () => true);

        Assert.That(generator.Stop(), Is.True,
            "Run disposed an instance it does not own, so the owner's later calls run against a disposed source");
    }
}
