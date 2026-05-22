// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Metric;
using Nethermind.Core.Test;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    [TestFixture]
    public class JsonRpcLocalStatsTests
    {
        private TestLogger _testLogger = null!;

        private readonly JsonRpcConfig _config = new();

        private ManualTimestamper _manualTimestamper = null!;

        private OneLoggerLogManager _logManager = null!;

        [SetUp]
        public void Setup()
        {
            _manualTimestamper = new ManualTimestamper(DateTime.MinValue);
            _testLogger = new TestLogger();
            _logManager = new OneLoggerLogManager(new(_testLogger));
        }

        [Test]
        public void Success_average_is_fine()
        {
            JsonRpcLocalStats localStats = CreateStats();
            localStats.ReportCall("A", 100, true);
            localStats.ReportCall("A", 200, true);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            CheckLogLine("A|2|0.150|0.200|0|0.000|0.000|");
            CheckLogLine("TOTAL|2|0.150|0.200|0|0.000|0.000|");
        }

        [Test]
        public void Single_average_is_fine()
        {
            JsonRpcLocalStats localStats = CreateStats();
            localStats.ReportCall("A", 100, true);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            CheckLogLine("A|1|0.100|0.100|0|0.000|0.000|");
            CheckLogLine("TOTAL|1|0.100|0.100|0|0.000|0.000|");
        }

        [Test]
        public void Swaps_properly()
        {
            JsonRpcLocalStats localStats = CreateStats();
            localStats.ReportCall("A", 100, true);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            CheckLogLine("A|1|0.100|0.100|0|0.000|0.000|");
            _testLogger.LogList.Clear();
            MakeTimePass();
            localStats.ReportCall("A", 500, true);
            CheckLogLine("A|1|0.300|0.300|0|0.000|0.000|");
            _testLogger.LogList.Clear();
            MakeTimePass();
            localStats.ReportCall("A", 700, true);
            CheckLogLine("A|1|0.500|0.500|0|0.000|0.000|");
            _testLogger.LogList.Clear();
        }

        [Test]
        public void Calls_do_not_delay_report()
        {
            JsonRpcLocalStats localStats = CreateStats();
            for (int i = 0; i < 100; i++)
            {
                localStats.ReportCall("A", 300, true);
                MakeTimePass(60);
            }

            WaitForLog();
            _testLogger.LogList.Should().HaveCountGreaterThan(0);
        }

        [Test]
        public void Does_not_report_when_info_not_enabled()
        {
            _testLogger = new TestLogger { IsInfo = false };

            JsonRpcLocalStats localStats = CreateStats(logManager: new OneLoggerLogManager(new(_testLogger)));
            localStats.ReportCall("A", 100, true);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            _testLogger.LogList.Should().HaveCount(0);
        }

        [Test]
        public void Does_not_report_when_nothing_to_report()
        {
            JsonRpcLocalStats localStats = CreateStats();
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            _testLogger.LogList.Should().HaveCount(0);
        }

        [Test]
        public void Multiple_have_no_decimal_places()
        {
            JsonRpcLocalStats localStats = CreateStats();
            localStats.ReportCall("A", 30, true);
            localStats.ReportCall("A", 20, true);
            localStats.ReportCall("A", 50, true);
            localStats.ReportCall("A", 60, false);
            localStats.ReportCall("A", 40, false);
            localStats.ReportCall("A", 100, false);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            CheckLogLine("A|3|0.033|0.050|3|0.067|0.100|");
            CheckLogLine("TOTAL|3|0.033|0.050|3|0.067|0.100|");
        }

        [Test]
        public void Single_of_each_is_fine()
        {
            JsonRpcLocalStats localStats = CreateStats();
            localStats.ReportCall("A", 25, true);
            localStats.ReportCall("A", 125, false);
            localStats.ReportCall("B", 75, true);
            localStats.ReportCall("B", 175, false);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            CheckLogLine("A|1|0.025|0.025|1|0.125|0.125|");
            CheckLogLine("B|1|0.075|0.075|1|0.175|0.175|");
            CheckLogLine("TOTAL|2|0.050|0.075|2|0.150|0.175|");
        }

        [Test]
        public void Orders_alphabetically()
        {
            JsonRpcLocalStats localStats = CreateStats();
            localStats.ReportCall("C", 1, true);
            localStats.ReportCall("A", 2, true);
            localStats.ReportCall("B", 3, false);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            WaitForLog();
            _testLogger.LogList[0].IndexOf("A   ", StringComparison.Ordinal).Should().BeLessThan(_testLogger.LogList[0].IndexOf("B   ", StringComparison.Ordinal));
            _testLogger.LogList[0].IndexOf("B   ", StringComparison.Ordinal).Should().BeLessThan(_testLogger.LogList[0].IndexOf("C   ", StringComparison.Ordinal));
        }

        [Test]
        public void Records_metric_when_per_method_enabled_even_without_info_logging()
        {
            RecordingMetricObserver observer = new();
            IMetricObserver previous = Metrics.JsonRpcCallLatencyMicros;
            Metrics.JsonRpcCallLatencyMicros = observer;
            try
            {
                TestLogger silentLogger = new() { IsInfo = false };
                OneLoggerLogManager silentLogManager = new(new(silentLogger));
                JsonRpcLocalStats localStats = CreateStats(new JsonRpcConfig { EnablePerMethodMetrics = true }, silentLogManager);

                localStats.ReportCall(new RpcReport("eth_call", 0, true), elapsedMicroseconds: 123);

                silentLogger.LogList.Should().BeEmpty();
                observer.Observations.Should().HaveCount(1);
                observer.Observations[0].Value.Should().Be(123);
                observer.Observations[0].Labels.Should().Equal("eth_call", "success");
            }
            finally
            {
                Metrics.JsonRpcCallLatencyMicros = previous;
            }
        }

        private sealed class RecordingMetricObserver : IMetricObserver
        {
            public List<(double Value, string[] Labels)> Observations { get; } = [];

            public void Observe(double value, IMetricLabels? labels = null) =>
                Observations.Add((value, labels?.Labels ?? Array.Empty<string>()));
        }

        private JsonRpcLocalStats CreateStats(IJsonRpcConfig? config = null, ILogManager? logManager = null) => new(_manualTimestamper, config ?? _config, logManager ?? _logManager);

        private void MakeTimePass(int seconds) => _manualTimestamper.UtcNow = _manualTimestamper.UtcNow.AddSeconds(seconds);

        private void MakeTimePass() => _manualTimestamper.UtcNow = _manualTimestamper.UtcNow.AddSeconds(_config.ReportIntervalSeconds + 1);

        private void CheckLogLine(string line)
        {
            bool hasLine = SpinWait.SpinUntil(
                () => _testLogger.LogList.Exists(l => l.Replace(" ", String.Empty).Contains(line)),
                TimeSpan.FromSeconds(5));

            hasLine.Should().BeTrue(string.Join(Environment.NewLine, _testLogger.LogList));
        }

        private void WaitForLog() =>
            SpinWait.SpinUntil(() => _testLogger.LogList.Count != 0, TimeSpan.FromSeconds(5))
                .Should().BeTrue();
    }
}
