//  Copyright (c) 2021 Demerzel Solutions Limited
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

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Consensus.AuRa
{
    public static class TaskExt
    {
        /// <summary>
        /// Guarantees to delay at least the specified delay. 
        ///  </summary>
        /// <param name="delay">Time to delay</param>
        /// <param name="token"></param>
        /// <remarks>Due to different resolution of timers on different systems, Task.Delay can return before specified delay.</remarks>
        /// <returns></returns>
        public static async Task DelayAtLeast(TimeSpan delay, CancellationToken token = default)
        {
            while (delay > TimeSpan.Zero)
            {
                var before = DateTimeOffset.Now;
                await Task.Delay(delay, token);
                delay -= (DateTimeOffset.Now - before);
            }
        }
    }
}
