// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Tools.Kute.Metrics;
using NSubstitute;
using NUnit.Framework;
using System.Text.Json.Nodes;

namespace Nethermind.Tools.Kute.Test;

public class MetricsTests
{
    private static JsonRpc.Request.Single Single(int id, string method = "test")
    {
        string json = $$"""{ "id": {{id}}, "method": "{{method}}", "params": [] }""";
        return new JsonRpc.Request.Single(JsonNode.Parse(json)!);
    }

    private static JsonRpc.Request.Batch Batch(params JsonRpc.Request.Single[] singles)
    {
        string json = $$"""[{{string.Join(", ", singles.Select(s => s.ToJsonString()))}}]""";
        return new JsonRpc.Request.Batch(JsonNode.Parse(json)!);
    }

    [Test]
    public async Task MemoryMetricsReporter_GeneratesValidReport()
    {
        MemoryMetricsReporter reporter = new();

        Timer totalTimer = new();
        using (totalTimer.Time())
        {
            JsonRpc.Request.Single single = Single(42, "method1");
            JsonRpc.Request.Batch batch = Batch(Single(43), Single(44), Single(45));

            Timer singleTimer = new();
            using (singleTimer.Time())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
            await reporter.Single(single, singleTimer.Elapsed);

            Timer batchTimer = new();
            using (batchTimer.Time())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }
            await reporter.Batch(batch, batchTimer.Elapsed);
        }

        await reporter.Total(totalTimer.Elapsed);

        MetricsReport report = reporter.Report();

        Assert.That(report.TotalTime, Is.GreaterThan(TimeSpan.FromMilliseconds(90)));
        Assert.That(report.TotalTime, Is.LessThan(TimeSpan.FromMilliseconds(110)));

        Assert.That(report.Singles, Has.Count.EqualTo(1));
        Assert.That(report.Singles, Does.ContainKey("method1"));
        Assert.That(report.Singles["method1"], Has.Count.EqualTo(1));
        Assert.That(report.Singles["method1"], Does.ContainKey("42"));

        Assert.That(report.Batches, Has.Count.EqualTo(1));
        Assert.That(report.Batches, Does.ContainKey("43:45"));
    }

    [Test]
    public async Task ComposedMetricsReporter_DelegatesToAllReporters()
    {
        IMetricsReporter A = Substitute.For<IMetricsReporter>();
        IMetricsReporter B = Substitute.For<IMetricsReporter>();
        JsonRpc.Request.Single single = Single(1);
        JsonRpc.Request.Batch batch = Batch(Single(2), Single(3));
        ComposedMetricsReporter reporter = new(A, B);

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
