using System.Collections.Generic;

namespace Nevermind.Discovery.RoutingTable
{
    public interface INodeBucket
    {
        int Distance { get; }
        int BucketSize { get; }
        SortedSet<NodeBucketItem> Items { get; }
        NodeAddResult AddNode(Node node);
        void RemoveNode(Node node);
    }
}