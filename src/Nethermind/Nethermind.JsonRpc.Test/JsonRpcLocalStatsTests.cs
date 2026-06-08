// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Events;
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

        private JsonRpcLocalStats _localStats = null!;

        [SetUp]
        public void Setup()
        {
            _manualTimestamper = new ManualTimestamper(DateTime.MinValue);
            _testLogger = new TestLogger();
            _logManager = new OneLoggerLogManager(new(_testLogger));
            _localStats = CreateStats();
        }

        [TestCase(new long[] { 100, 200 }, "A|2|0.150|0.200|0|0.000|0.000|", "TOTAL|2|0.150|0.200|0|0.000|0.000|", TestName = "Success average")]
        [TestCase(new long[] { 100 }, "A|1|0.100|0.100|0|0.000|0.000|", "TOTAL|1|0.100|0.100|0|0.000|0.000|", TestName = "Single average")]
        public async Task Success_average_is_fine(long[] handlingTimes, string methodLine, string totalLine)
        {
            Report("A", true, handlingTimes);

            MakeTimePass();
            _localStats.ReportCall("A", 300, true);
            await CheckLogLines(methodLine, totalLine);
        }

        [Test]
        public async Task Swaps_properly()
        {
            _localStats.ReportCall("A", 100, true);
            MakeTimePass();
            _localStats.ReportCall("A", 300, true);
            await CheckLogLine("A|1|0.100|0.100|0|0.000|0.000|");
            _testLogger.LogList.Clear();
            MakeTimePass();
            _localStats.ReportCall("A", 500, true);
            await CheckLogLine("A|1|0.300|0.300|0|0.000|0.000|");
            _testLogger.LogList.Clear();
            MakeTimePass();
            _localStats.ReportCall("A", 700, true);
            await CheckLogLine("A|1|0.500|0.500|0|0.000|0.000|");
            _testLogger.LogList.Clear();
        }

        [Test]
        public async Task Calls_do_not_delay_report()
        {
            for (int i = 0; i < 100; i++)
            {
                _localStats.ReportCall("A", 300, true);
                MakeTimePass(60);
            }

            await WaitForLog();
            Assert.That(_testLogger.LogList, Has.Count.GreaterThan(0));
        }

        [Test]
        public void Does_not_report_when_info_not_enabled()
        {
            _testLogger = new TestLogger { IsInfo = false };

            JsonRpcLocalStats localStats = CreateStats(logManager: new OneLoggerLogManager(new(_testLogger)));
            localStats.ReportCall("A", 100, true);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            Assert.That(_testLogger.LogList, Has.Count.EqualTo(0));
        }

        [Test]
        public void Does_not_report_when_nothing_to_report()
        {
            MakeTimePass();
            _localStats.ReportCall("A", 300, true);
            Assert.That(_testLogger.LogList, Has.Count.EqualTo(0));
        }

        [Test]
        public async Task Multiple_have_no_decimal_places()
        {
            Report("A", true, 30, 20, 50);
            Report("A", false, 60, 40, 100);
            MakeTimePass();
            _localStats.ReportCall("A", 300, true);
            await CheckLogLines("A|3|0.033|0.050|3|0.067|0.100|", "TOTAL|3|0.033|0.050|3|0.067|0.100|");
        }

        [Test]
        public async Task Single_of_each_is_fine()
        {
            _localStats.ReportCall("A", 25, true);
            _localStats.ReportCall("A", 125, false);
            _localStats.ReportCall("B", 75, true);
            _localStats.ReportCall("B", 175, false);
            MakeTimePass();
            _localStats.ReportCall("A", 300, true);
            await CheckLogLines(
                "A|1|0.025|0.025|1|0.125|0.125|",
                "B|1|0.075|0.075|1|0.175|0.175|",
                "TOTAL|2|0.050|0.075|2|0.150|0.175|");
        }

        [Test]
        public async Task Orders_alphabetically()
        {
            _localStats.ReportCall("C", 1, true);
            _localStats.ReportCall("A", 2, true);
            _localStats.ReportCall("B", 3, false);
            MakeTimePass();
            _localStats.ReportCall("A", 300, true);
            await WaitForLog();
            Assert.That(_testLogger.LogList[0].IndexOf("A   ", StringComparison.Ordinal), Is.LessThan(_testLogger.LogList[0].IndexOf("B   ", StringComparison.Ordinal)));
            Assert.That(_testLogger.LogList[0].IndexOf("B   ", StringComparison.Ordinal), Is.LessThan(_testLogger.LogList[0].IndexOf("C   ", StringComparison.Ordinal)));
        }

        [Test]
        public async Task Orders_methods_ordinally()
        {
            CultureInfo originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

                _localStats.ReportCall("_", 1, true);
                _localStats.ReportCall("A", 2, true);
                MakeTimePass();
                _localStats.ReportCall("trigger", 300, true);
                await WaitForLog();

                Assert.That(
                    _testLogger.LogList[0].IndexOf("A   ", StringComparison.Ordinal),
                    Is.LessThan(_testLogger.LogList[0].IndexOf("_   ", StringComparison.Ordinal)));
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }
        }

        [Test]
        public void Records_metric_when_per_method_enabled_even_without_info_logging()
        {
            RecordingMetricObserver observer = new();
            IMetricObserver previous = Metrics.JsonRpcCallDurationMicros;
            Metrics.JsonRpcCallDurationMicros = observer;
            try
            {
                TestLogger silentLogger = new() { IsInfo = false };
                OneLoggerLogManager silentLogManager = new(new(silentLogger));
                JsonRpcLocalStats localStats = CreateStats(new JsonRpcConfig { EnablePerMethodMetrics = true }, silentLogManager);

                localStats.ReportCall(new RpcReport("eth_call", 0, true), elapsedMicroseconds: 123);

                Assert.That(silentLogger.LogList, Is.Empty);
                Assert.That(observer.Observations, Has.Count.EqualTo(1));
                Assert.That(observer.Observations[0].Value, Is.EqualTo(123));
                Assert.That(observer.Observations[0].Labels, Is.EqualTo(new[] { "eth_call", "success" }));
            }
            finally
            {
                Metrics.JsonRpcCallDurationMicros = previous;
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

        private void Report(string method, bool success, params long[] handlingTimes)
        {
            foreach (long handlingTime in handlingTimes) _localStats.ReportCall(method, handlingTime, success);
        }

        private async Task CheckLogLines(params string[] lines)
        {
            foreach (string line in lines) await CheckLogLine(line);
        }

        private async Task CheckLogLine(string line)
        {
            bool hasLine = await Wait.ForCondition(
                () => _testLogger.LogList.Exists(l => l.Replace(" ", String.Empty).Contains(line)),
                TimeSpan.FromSeconds(30));

            Assert.That(hasLine, Is.True, string.Join(Environment.NewLine, _testLogger.LogList));
        }

        private async Task WaitForLog() =>
            Assert.That(await Wait.ForCondition(() => _testLogger.LogList.Count != 0, TimeSpan.FromSeconds(30)), Is.True);
    }
}
