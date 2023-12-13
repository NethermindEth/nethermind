// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
