using System;
using System.Collections.Generic;
using System.Threading;
using Cortex.BeaconNode.Services;

namespace Cortex.BeaconNode.Tests.Helpers
{
    public class FastTestClock : IClock
    {
        private readonly Queue<DateTimeOffset> _timeValues;

        public FastTestClock(IEnumerable<DateTimeOffset> timeValues)
        {
            CompleteWaitHandle = new ManualResetEvent(false);
            _timeValues = new Queue<DateTimeOffset>(timeValues);
        }

        public ManualResetEvent CompleteWaitHandle { get; }

        public DateTimeOffset Now()
        {
            var next = _timeValues.Dequeue();
            if (_timeValues.Count == 0)
            {
                CompleteWaitHandle.Set();
            }
            return next;
        }
    }
}
