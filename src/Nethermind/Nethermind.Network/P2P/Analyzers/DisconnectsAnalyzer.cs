// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    public class DisconnectsAnalyzer : IDisconnectsAnalyzer, IDisposable
    {
        private const int ReasonCount = byte.MaxValue + 1;

        private readonly Timer _timer;
        private readonly ILogger _logger;
        private readonly StringBuilder _builder = new();

        // One stable counter per (type, reason). A flush takes each counter with Interlocked.Exchange,
        // so every increment lands in exactly one flush without a lock or buffer swap.
        private readonly int[] _disconnects = new int[Enum.GetValues<DisconnectType>().Length * ReasonCount];

        private int _disconnectCount = 0;

        public DisconnectsAnalyzer(ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<DisconnectsAnalyzer>() ?? throw new ArgumentNullException(nameof(logManager));

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
            _builder.AppendLine("Disconnect reasons:");
            for (int i = 0; i < _disconnects.Length; i++)
            {
                int count = Interlocked.Exchange(ref _disconnects[i], 0);
                if (count == 0) continue;

                _builder.AppendLine(
                    "  "
                    + ((DisconnectType)(i / ReasonCount)).ToString().PadRight(8)
                    + ((DisconnectReason)(i % ReasonCount)).ToString().PadRight(24)
                    + count.ToString().PadLeft(4));
            }

            _logger.Info(_builder.ToString());
            _builder.Clear();
            _timer.Enabled = true;
        }

        public void ReportDisconnect(DisconnectReason reason, DisconnectType type, string? details)
        {
            Interlocked.Increment(ref _disconnectCount);
            Interlocked.Increment(ref _disconnects[(int)type * ReasonCount + (byte)reason]);

            if (type == DisconnectType.Local && details is not null)
            {
                _logger.Warn(details);
            }
        }

        public void Dispose()
        {
            _timer.Elapsed -= TimerOnElapsed;
            _timer.Dispose();
        }
    }
}
