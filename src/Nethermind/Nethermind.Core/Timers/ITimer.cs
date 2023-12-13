// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Timers
{
    public interface ITimer : IDisposable
    {
        /// <summary>
        /// Gets or sets a Boolean indicating whether the <see cref="ITimer"/> should raise the <see cref="Elapsed"/> event only once (false) or repeatedly (true).
        /// </summary>
        bool AutoReset { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the <see cref="ITimer"/> should raise the <see cref="Elapsed"/> event.
        /// </summary>
        bool Enabled { get; set; }

        /// <summary>
        /// Gets or sets the interval, at which to raise the <see cref="Elapsed"/> event.
        /// </summary>
        TimeSpan Interval { get; set; }

        /// <summary>
        /// Gets or sets the interval, expressed in milliseconds, at which to raise the <see cref="Elapsed"/> event.
        /// </summary>
        double IntervalMilliseconds { get; set; }

        /// <summary>
        /// Starts raising the <see cref="Elapsed"/> event by setting <see cref="Enabled"/> to true.
        /// </summary>
        void Start();

        /// <summary>
        /// Stops raising the <see cref="Elapsed"/> event by setting <see cref="Enabled"/> to false.
        /// </summary>
        void Stop();

        /// <summary>
        /// Occurs when the interval elapses.
        /// </summary>
        event EventHandler Elapsed;
    }
}
