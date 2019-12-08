using System;

namespace Nethermind.BeaconNode.Services
{
    public interface IClock
    {
        DateTimeOffset UtcNow();
    }
}
