// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using NUnit.Framework;
using Nethermind.Tools.Kute.Metrics;
using NSubstitute;
using System.Text.Json;

namespace Nethermind.Tools.Kute.Test;

public class MetricsTests
{
    private static JsonRpc.Request.Single Single(int id)
    {
        var json = $$"""{ "id": {{id}}, "method": "test", "params": [] }""";
        return new JsonRpc.Request.Single(JsonDocument.Parse(json));
    }

    private static JsonRpc.Request.Batch Batch(params JsonRpc.Request.Single[] singles)
    {
        var json = $$"""[{{string.Join(", ", singles.Select(s => s.ToJsonString()))}}]""";
        return new JsonRpc.Request.Batch(JsonDocument.Parse(json));
    }

    [Test]
    public async Task MemoryMetricsReporter_GeneratesValidReport()
    {
        var reporter = new MemoryMetricsReporter();

        var totalTimer = new Timer();
        using (totalTimer.Time())
        {
            var single = Single(42);
            var requestTimer = new Timer();

            using (requestTimer.Time())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            await reporter.Single(single, requestTimer.Elapsed);
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
        var single = Single(1);
        var batch = Batch(Single(2), Single(3));
        var reporter = new ComposedMetricsReporter(A, B);

        await reporter.Message();
        await reporter.Response();
        await reporter.Succeeded();
        await reporter.Failed();
        await reporter.Ignored();
        await reporter.Single(single, TimeSpan.FromMilliseconds(100));
        await reporter.Batch(batch, TimeSpan.FromMilliseconds(200));
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

        await A.Received().Single(single, Arg.Any<TimeSpan>());
        await B.Received().Single(single, Arg.Any<TimeSpan>());

        await A.Received().Batch(batch, Arg.Any<TimeSpan>());
        await B.Received().Batch(batch, Arg.Any<TimeSpan>());

        await A.Received().Total(Arg.Any<TimeSpan>());
        await B.Received().Total(Arg.Any<TimeSpan>());
    }
}
