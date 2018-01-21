using System.Collections.Generic;
using System.Linq;

namespace Nevermind.Discovery.RoutingTable
{
    public class NodeBucket : INodeBucket
    {
        public NodeBucket(int distance, int bucketSize)
        {
            Items = new SortedSet<NodeBucketItem>(new LastContactTimeComparer());
            Distance = distance;
            BucketSize = bucketSize;
        }

        /// <summary>
        /// Distance from Master Node
        /// </summary>
        public int Distance { get; }
        public int BucketSize { get; }
        public SortedSet<NodeBucketItem> Items { get; }

        public NodeAddResult AddNode(Node node)
        {
            if (Items.Count < BucketSize)
            {
                var item = new NodeBucketItem(node);
                if (!Items.Contains(item))
                {
                    Items.Add(item);
                }                
                return NodeAddResult.Added();
            }

            var evictionCandidate = GetEvictionCandidate();
            return NodeAddResult.Full(evictionCandidate);
        }

        public void RemoveNode(Node node)
        {
            var item = new NodeBucketItem(node);
            if (Items.Contains(item))
            {
                Items.Remove(item);
            }
        }

        private NodeBucketItem GetEvictionCandidate()
        {
            return Items.Last();
        }

        private class LastContactTimeComparer : IComparer<NodeBucketItem>
        {
            public int Compare(NodeBucketItem x, NodeBucketItem y)
            {
                if (x == null && y == null)
                {
                    return 0;
                }

                if (x == null)
                {
                    return -1;
                }

                if (y == null)
                {
                    return 1;
                }

                return x.LastContactTime.CompareTo(y.LastContactTime);
            }
        }
    }
}