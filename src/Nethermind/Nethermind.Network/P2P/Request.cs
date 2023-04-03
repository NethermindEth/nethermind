// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Nethermind.Network.P2P
{
    public class Request<TMsg, TResult>
    {
        public Request(TMsg message)
        {
            CompletionSource = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Message = message;
        }

        public void StartMeasuringTime()
        {
            Stopwatch = Stopwatch.StartNew();
        }

        public long FinishMeasuringTime()
        {
            Stopwatch.Stop();
            return Stopwatch.ElapsedMilliseconds;
        }

        private Stopwatch Stopwatch { get; set; }

        public TimeSpan Elapsed => Stopwatch.Elapsed;
        public long ResponseSize { get; set; }
        public TMsg Message { get; }
        public TaskCompletionSource<TResult> CompletionSource { get; }
    }
}
