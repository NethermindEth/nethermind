//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
