// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

        private readonly DateTime _startTime = DateTime.MinValue;
        private OneLoggerLogManager _logManager = null!;

        [SetUp]
        public void Setup()
        {
            _manualTimestamper = new ManualTimestamper(_startTime);
            _testLogger = new TestLogger();
            _logManager = new OneLoggerLogManager(new(_testLogger));
        }

        [Test]
        public async Task Success_average_is_fine()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            await localStats.ReportCall("A", 100, true);
            await localStats.ReportCall("A", 200, true);
            MakeTimePass();
            await localStats.ReportCall("A", 300, true);
            CheckLogLine("A|2|0.150|0.200|0|0.000|0.000|");
            CheckLogLine("TOTAL|2|0.150|0.200|0|0.000|0.000|");
        }

        [Test]
        public async Task Single_average_is_fine()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            await localStats.ReportCall("A", 100, true);
            MakeTimePass();
            await localStats.ReportCall("A", 300, true);
            CheckLogLine("A|1|0.100|0.100|0|0.000|0.000|");
            CheckLogLine("TOTAL|1|0.100|0.100|0|0.000|0.000|");
        }

        [Test]
        public async Task Swaps_properly()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            await localStats.ReportCall("A", 100, true);
            MakeTimePass();
            await localStats.ReportCall("A", 300, true);
            CheckLogLine("A|1|0.100|0.100|0|0.000|0.000|");
            _testLogger.LogList.Clear();
            MakeTimePass();
            await localStats.ReportCall("A", 500, true);
            CheckLogLine("A|1|0.300|0.300|0|0.000|0.000|");
            _testLogger.LogList.Clear();
            MakeTimePass();
            await localStats.ReportCall("A", 700, true);
            CheckLogLine("A|1|0.500|0.500|0|0.000|0.000|");
            _testLogger.LogList.Clear();
        }

        [Test]
        public async Task Calls_do_not_delay_report()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            for (int i = 0; i < 100; i++)
            {
                await localStats.ReportCall("A", 300, true);
                MakeTimePass(60);
            }

            Assert.That((_testLogger.LogList).Count, Is.GreaterThan(0));
        }

        [Test]
        public void Does_not_report_when_info_not_enabled()
        {
            _testLogger = new TestLogger();
            _testLogger.IsInfo = false;

            OneLoggerLogManager logManager = new(new(_testLogger));
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, logManager);
            localStats.ReportCall("A", 100, true);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            Assert.That((_testLogger.LogList).Count, Is.EqualTo(0));
        }

        [Test]
        public void Does_not_report_when_nothing_to_report()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            Assert.That((_testLogger.LogList).Count, Is.EqualTo(0));
        }

        [Test]
        public async Task Multiple_have_no_decimal_places()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            await localStats.ReportCall("A", 30, true);
            await localStats.ReportCall("A", 20, true);
            await localStats.ReportCall("A", 50, true);
            await localStats.ReportCall("A", 60, false);
            await localStats.ReportCall("A", 40, false);
            await localStats.ReportCall("A", 100, false);
            MakeTimePass();
            await localStats.ReportCall("A", 300, true);
            CheckLogLine("A|3|0.033|0.050|3|0.067|0.100|");
            CheckLogLine("TOTAL|3|0.033|0.050|3|0.067|0.100|");
        }

        [Test]
        public async Task Single_of_each_is_fine()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            await localStats.ReportCall("A", 25, true);
            await localStats.ReportCall("A", 125, false);
            await localStats.ReportCall("B", 75, true);
            await localStats.ReportCall("B", 175, false);
            MakeTimePass();
            await localStats.ReportCall("A", 300, true);
            CheckLogLine("A|1|0.025|0.025|1|0.125|0.125|");
            CheckLogLine("B|1|0.075|0.075|1|0.175|0.175|");
            CheckLogLine("TOTAL|2|0.050|0.075|2|0.150|0.175|");
        }

        [Test]
        public async Task Orders_alphabetically()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            await localStats.ReportCall("C", 1, true);
            await localStats.ReportCall("A", 2, true);
            await localStats.ReportCall("B", 3, false);
            MakeTimePass();
            await localStats.ReportCall("A", 300, true);
            Assert.That(_testLogger.LogList[0].IndexOf("A   ", StringComparison.Ordinal), Is.LessThan(_testLogger.LogList[0].IndexOf("B   ", StringComparison.Ordinal)));
            Assert.That(_testLogger.LogList[0].IndexOf("B   ", StringComparison.Ordinal), Is.LessThan(_testLogger.LogList[0].IndexOf("C   ", StringComparison.Ordinal)));
        }

        [Test]
        public async Task Records_metric_when_per_method_enabled_even_without_info_logging()
        {
            RecordingMetricObserver observer = new();
            IMetricObserver previous = Metrics.JsonRpcCallLatencyMicros;
            Metrics.JsonRpcCallLatencyMicros = observer;
            try
            {
                TestLogger silentLogger = new() { IsInfo = false };
                OneLoggerLogManager silentLogManager = new(new(silentLogger));
                JsonRpcConfig config = new() { EnablePerMethodMetrics = true };
                JsonRpcLocalStats localStats = new(_manualTimestamper, config, silentLogManager);

                await localStats.ReportCall(new RpcReport("eth_call", 0, true), elapsedMicroseconds: 123);

                Assert.That(silentLogger.LogList, Is.Empty);
                Assert.That((observer.Observations).Count, Is.EqualTo(1));
                Assert.That(observer.Observations[0].Value, Is.EqualTo(123));
                Assert.That(observer.Observations[0].Labels, Is.EqualTo(new[] { "eth_call", "success" }));
            }
            finally
            {
                Metrics.JsonRpcCallLatencyMicros = previous;
            }
        }

        private sealed class RecordingMetricObserver : IMetricObserver
        {
            public List<(double Value, string[] Labels)> Observations { get; } = new();

            public void Observe(double value, IMetricLabels? labels = null) =>
                Observations.Add((value, labels?.Labels ?? Array.Empty<string>()));
        }

        private void MakeTimePass(int seconds) => _manualTimestamper.UtcNow = _manualTimestamper.UtcNow.AddSeconds(seconds);

        private void MakeTimePass() => _manualTimestamper.UtcNow = _manualTimestamper.UtcNow.AddSeconds(_config.ReportIntervalSeconds + 1);

        private void CheckLogLine(string line) => Assert.That(_testLogger.LogList.Exists(l => l.Replace(" ", String.Empty).Contains(line)), Is.True, string.Join(Environment.NewLine, _testLogger.LogList));
    }
}
