using System;
using Cortex.BeaconNode.Services;

namespace Cortex.BeaconNode.MockedStart
{
    /// <summary>
    /// Clock that runs at normal pace, but starting at the specified unix time.
    /// </summary>
    public class QuickStartClock : IClock
    {
        private readonly TimeSpan _adjustment;

        public QuickStartClock(ulong startTime)
        {
            _adjustment = TimeSpan.FromSeconds((long)startTime - DateTimeOffset.Now.ToUnixTimeSeconds());
        }

        public DateTimeOffset Now() => DateTimeOffset.Now + _adjustment;
    }
}
