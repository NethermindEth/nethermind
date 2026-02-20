// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Xdc
{
    internal class TimeoutTimer : IDisposable, ITimeoutTimer
    {
        private readonly System.Timers.Timer timer;
        private readonly ITimeoutCertificateManager _timeoutCertificateManager;

        public TimeoutTimer(ITimeoutCertificateManager timeoutCertificateManager)
        {
            timer = new System.Timers.Timer();
            timer.AutoReset = false;
            timer.Elapsed += (s, e) => Callback();
            _timeoutCertificateManager = timeoutCertificateManager;
        }

        public void Start(TimeSpan period)
        {
            timer.Interval = period.TotalMilliseconds;
            timer.Enabled = true;
            timer.Start();
        }

        public void Reset(TimeSpan period)
        {
            timer.Interval = period.TotalMilliseconds;
            timer.Enabled = true;
        }

        public void Dispose()
        {
            timer.Elapsed -= (s, e) => Callback();
            timer?.Dispose();
        }

        public void TriggerTimeout()
        {
            Callback();
        }
        private void Callback()
        {
            _timeoutCertificateManager.OnCountdownTimer();
        }
    }
}
