using System;

namespace Cortex.BeaconNode.Services
{
    public interface IClock
    {
        DateTimeOffset Now();
    }
}
