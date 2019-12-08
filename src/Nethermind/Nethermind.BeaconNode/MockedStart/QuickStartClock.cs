using System;
using Nethermind.BeaconNode.Services;

namespace Nethermind.BeaconNode.MockedStart
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

        public DateTimeOffset UtcNow() => DateTimeOffset.Now + _adjustment;
    }
}
