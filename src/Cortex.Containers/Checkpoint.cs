using Epoch = System.UInt64;
using Hash = System.Byte; // Byte32

namespace Cortex.Containers
{
    public class Checkpoint
    {
        public Epoch Epoch { get; }
        public Hash[] Root { get; }
    }
}
