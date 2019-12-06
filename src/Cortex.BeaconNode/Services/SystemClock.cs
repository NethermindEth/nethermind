using System;

namespace Cortex.BeaconNode.Services
{
    public class SystemClock : IClock
    {
        public DateTimeOffset Now() => DateTimeOffset.Now;
    }
}
