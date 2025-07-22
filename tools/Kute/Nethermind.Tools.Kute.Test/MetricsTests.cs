// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;
using Nethermind.Tools.Kute.Metrics;
using NSubstitute;

namespace Nethermind.Tools.Kute.Test;

public class MetricsTests
{
    [Test]
    public async Task MemoryMetricsReporter_GeneratesValidReport()
    {
        var reporter = new MemoryMetricsReporter();

        var totalTimer = new Timer();
        using (totalTimer.Time())
        {
            var id = 42;
            var requestTimer = new Timer();

            using (requestTimer.Time())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            await reporter.Single(id, requestTimer.Elapsed);
        }

        await reporter.Total(totalTimer.Elapsed);

        var report = reporter.Report();

        report.TotalTime.Should().BeGreaterThan(TimeSpan.FromMilliseconds(90));
        report.TotalTime.Should().BeLessThan(TimeSpan.FromMilliseconds(110));
        report.Singles.Should().HaveCount(1);
    }

    [Test]
    public async Task ComposedMetricsReporter_DelegatesToAllReporters()
    {
        var A = Substitute.For<IMetricsReporter>();
        var B = Substitute.For<IMetricsReporter>();
        var reporter = new ComposedMetricsReporter(A, B);

        await reporter.Message();
        await reporter.Response();
        await reporter.Succeeded();
        await reporter.Failed();
        await reporter.Ignored();
        await reporter.Batch(1, TimeSpan.FromMilliseconds(100));
        await reporter.Single(2, TimeSpan.FromMilliseconds(200));
        await reporter.Total(TimeSpan.FromMilliseconds(300));

        await A.Received().Message();
        await B.Received().Message();

        await A.Received().Response();
        await B.Received().Response();

        await A.Received().Succeeded();
        await B.Received().Succeeded();

        await A.Received().Failed();
        await B.Received().Failed();

        await A.Received().Ignored();
        await B.Received().Ignored();

        await A.Received().Batch(1, Arg.Any<TimeSpan>());
        await B.Received().Batch(1, Arg.Any<TimeSpan>());

        await A.Received().Single(2, Arg.Any<TimeSpan>());
        await B.Received().Single(2, Arg.Any<TimeSpan>());

        await A.Received().Total(Arg.Any<TimeSpan>());
        await B.Received().Total(Arg.Any<TimeSpan>());
    }
}
