using System;

namespace Nethermind.BeaconNode.Services
{
    public class SystemClock : IClock
    {
        public DateTimeOffset UtcNow() => DateTimeOffset.UtcNow;
    }
}
