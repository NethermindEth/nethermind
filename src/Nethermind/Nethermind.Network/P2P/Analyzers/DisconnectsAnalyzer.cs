// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Timers;
using Nethermind.Logging;
using Nethermind.Stats.Model;
using Timer = System.Timers.Timer;

namespace Nethermind.Network.P2P.Analyzers
{
    /// <summary>
    /// This class is created to help diagnose network disconnections.
    /// </summary>
    public class DisconnectsAnalyzer : IDisconnectsAnalyzer
    {
        private readonly Timer _timer;
        private readonly ILogger _logger;
        private readonly StringBuilder _builder = new();

        private readonly ConcurrentDictionary<DisconnectCategory, int> _disconnectsA = new();
        private readonly ConcurrentDictionary<DisconnectCategory, int> _disconnectsB = new();

        private ConcurrentDictionary<DisconnectCategory, int> _disconnects;

        private int _disconnectCount = 0;

        private readonly struct DisconnectCategory
        {
            public DisconnectCategory(EthDisconnectReason reason, DisconnectType type)
            {
                Reason = reason;
                Type = type;
            }

            public EthDisconnectReason Reason { get; }

            public DisconnectType Type { get; }
        }

        public DisconnectsAnalyzer(ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _disconnects = _disconnectsA;

            _timer = new Timer(10000);
            _timer.Elapsed += TimerOnElapsed;
            _timer.AutoReset = false;
            _timer.Start();
        }

        public DisconnectsAnalyzer WithIntervalOverride(int interval)
        {
            _timer.Interval = interval;
            _timer.Stop();
            _timer.Start();
            return this;
        }

        private void TimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            ConcurrentDictionary<DisconnectCategory, int> localCopy = _disconnects;
            _disconnects = ReferenceEquals(_disconnects, _disconnectsA) ? _disconnectsB : _disconnectsA;

            _builder.AppendLine("Disconnect reasons:");
            foreach ((DisconnectCategory key, int value) in localCopy)
            {
                _builder.AppendLine(
                    "  "
                    + key.Type.ToString().PadRight(8)
                    + key.Reason.ToString().PadRight(24)
                    + value.ToString().PadLeft(4));
            }

            localCopy.Clear();

            _logger.Info(_builder.ToString());
            _builder.Clear();
            _timer.Enabled = true;
        }

        public void ReportDisconnect(EthDisconnectReason reason, DisconnectType type, string? details)
        {
            Interlocked.Increment(ref _disconnectCount);
            _disconnects.AddOrUpdate(new DisconnectCategory(reason, type), _ => 1, (_, i) => i + 1);

            if (type == DisconnectType.Local && details is not null)
            {
                _logger.Warn(details);
            }
        }
    }
}
