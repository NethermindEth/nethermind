using Microsoft.Extensions.Logging;

namespace Cortex.BeaconNode
{
    public static class HostEvent
    {
        // 1bxx preliminary
        public static readonly EventId WorkerStarted = new EventId(1000, nameof(WorkerStarted));
    }
}
