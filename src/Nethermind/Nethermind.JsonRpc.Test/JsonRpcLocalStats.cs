// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    [TestFixture]
    public class JsonRpcLocalStatsTests
    {
        private TestLogger _testLogger = null!;

        private JsonRpcConfig _config = new();

        private ManualTimestamper _manualTimestamper = null!;

        private DateTime _startTime = DateTime.MinValue;
        private OneLoggerLogManager _logManager = null!;

        [SetUp]
        public void Setup()
        {
            _manualTimestamper = new ManualTimestamper(_startTime);
            _testLogger = new TestLogger();
            _logManager = new OneLoggerLogManager(_testLogger);
        }

        [Test]
        public async Task Success_average_is_fine()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            await localStats.ReportCall("A", 100, true);
            await localStats.ReportCall("A", 200, true);
            MakeTimePass();
            await localStats.ReportCall("A", 300, true);
            CheckLogLine("A|2|150|200|0|0|0|");
            CheckLogLine("TOTAL|2|150|200|0|0|0|");
        }

        [Test]
        public async Task Single_average_is_fine()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            await localStats.ReportCall("A", 100, true);
            MakeTimePass();
            await localStats.ReportCall("A", 300, true);
            CheckLogLine("A|1|100|100|0|0|0|");
            CheckLogLine("TOTAL|1|100|100|0|0|0|");
        }

        [Test]
        public async Task Swaps_properly()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            await localStats.ReportCall("A", 100, true);
            MakeTimePass();
            await localStats.ReportCall("A", 300, true);
            CheckLogLine("A|1|100|100|0|0|0|");
            _testLogger.LogList.Clear();
            MakeTimePass();
            await localStats.ReportCall("A", 500, true);
            CheckLogLine("A|1|300|300|0|0|0|");
            _testLogger.LogList.Clear();
            MakeTimePass();
            await localStats.ReportCall("A", 700, true);
            CheckLogLine("A|1|500|500|0|0|0|");
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

            _testLogger.LogList.Should().HaveCountGreaterThan(0);
        }

        [Test]
        public void Does_not_report_when_info_not_enabled()
        {
            _testLogger = new TestLogger();
            _testLogger.IsInfo = false;

            OneLoggerLogManager logManager = new(_testLogger);
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, logManager);
            localStats.ReportCall("A", 100, true);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            _testLogger.LogList.Should().HaveCount(0);
        }

        [Test]
        public void Does_not_report_when_nothing_to_report()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            _testLogger.LogList.Should().HaveCount(0);
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
            CheckLogLine("A|3|33|50|3|67|100|");
            CheckLogLine("TOTAL|3|33|50|3|67|100|");
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
            CheckLogLine("A|1|25|25|1|125|125|");
            CheckLogLine("B|1|75|75|1|175|175|");
            CheckLogLine("TOTAL|2|50|75|2|150|175|");
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
            _testLogger.LogList[0].IndexOf("A   ", StringComparison.InvariantCulture).Should().BeLessThan(_testLogger.LogList[0].IndexOf("B   ", StringComparison.InvariantCulture));
            _testLogger.LogList[0].IndexOf("B   ", StringComparison.InvariantCulture).Should().BeLessThan(_testLogger.LogList[0].IndexOf("C   ", StringComparison.InvariantCulture));
        }

        private void MakeTimePass(int seconds)
        {
            _manualTimestamper.UtcNow = _manualTimestamper.UtcNow.AddSeconds(seconds);
        }

        private void MakeTimePass()
        {
            _manualTimestamper.UtcNow = _manualTimestamper.UtcNow.AddSeconds(_config.ReportIntervalSeconds + 1);
        }

        private void CheckLogLine(string line)
        {
            _testLogger.LogList.Exists(l => l.Replace(" ", String.Empty).Contains(line)).Should().BeTrue(string.Join(Environment.NewLine, _testLogger.LogList));
        }
    }
}
