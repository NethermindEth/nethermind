using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Types;

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
