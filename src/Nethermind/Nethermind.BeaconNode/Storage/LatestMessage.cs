using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode.Storage
{
    // Data Class
    public class LatestMessage
    {
        public LatestMessage(Epoch epoch, Hash32 root)
        {
            Epoch = epoch;
            Root = root;
        }

        public Epoch Epoch { get; }
        public Hash32 Root { get; }
    }
}
