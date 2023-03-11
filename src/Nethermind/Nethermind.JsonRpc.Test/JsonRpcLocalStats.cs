// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        public void Success_average_is_fine()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            localStats.ReportCall("A", 100, true);
            localStats.ReportCall("A", 200, true);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            CheckLogLine("A|2|150|200|0|0|0|");
            CheckLogLine("TOTAL|2|150|200|0|0|0|");
        }

        [Test]
        public void Single_average_is_fine()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            localStats.ReportCall("A", 100, true);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            CheckLogLine("A|1|100|100|0|0|0|");
            CheckLogLine("TOTAL|1|100|100|0|0|0|");
        }

        [Test]
        public void Swaps_properly()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            localStats.ReportCall("A", 100, true);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            CheckLogLine("A|1|100|100|0|0|0|");
            _testLogger.LogList.Clear();
            MakeTimePass();
            localStats.ReportCall("A", 500, true);
            CheckLogLine("A|1|300|300|0|0|0|");
            _testLogger.LogList.Clear();
            MakeTimePass();
            localStats.ReportCall("A", 700, true);
            CheckLogLine("A|1|500|500|0|0|0|");
            _testLogger.LogList.Clear();
        }

        [Test]
        public void Calls_do_not_delay_report()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            for (int i = 0; i < 100; i++)
            {
                localStats.ReportCall("A", 300, true);
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
        public void Multiple_have_no_decimal_places()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            localStats.ReportCall("A", 30, true);
            localStats.ReportCall("A", 20, true);
            localStats.ReportCall("A", 50, true);
            localStats.ReportCall("A", 60, false);
            localStats.ReportCall("A", 40, false);
            localStats.ReportCall("A", 100, false);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            CheckLogLine("A|3|33|50|3|67|100|");
            CheckLogLine("TOTAL|3|33|50|3|67|100|");
        }

        [Test]
        public void Single_of_each_is_fine()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            localStats.ReportCall("A", 25, true);
            localStats.ReportCall("A", 125, false);
            localStats.ReportCall("B", 75, true);
            localStats.ReportCall("B", 175, false);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
            CheckLogLine("A|1|25|25|1|125|125|");
            CheckLogLine("B|1|75|75|1|175|175|");
            CheckLogLine("TOTAL|2|50|75|2|150|175|");
        }

        [Test]
        public void Orders_alphabetically()
        {
            JsonRpcLocalStats localStats = new(_manualTimestamper, _config, _logManager);
            localStats.ReportCall("C", 1, true);
            localStats.ReportCall("A", 2, true);
            localStats.ReportCall("B", 3, false);
            MakeTimePass();
            localStats.ReportCall("A", 300, true);
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
