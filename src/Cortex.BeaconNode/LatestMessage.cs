using Epoch = System.UInt64;
using Hash = System.Byte; // Byte32

namespace Cortex.BeaconNode
{
    // Data Class
    public class LatestMessage
    {
        public Epoch Epoch { get; }
        public Hash Root { get; }
    }
}
