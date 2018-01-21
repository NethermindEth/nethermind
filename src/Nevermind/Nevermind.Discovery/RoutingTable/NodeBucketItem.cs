using System;

namespace Nevermind.Discovery.RoutingTable
{
    public class NodeBucketItem : INodeBucketItem
    {
        public NodeBucketItem(Node node)
        {
            Node = node;
            LastContactTime = DateTime.UtcNow;
        }

        public Node Node { get; }
        public DateTime LastContactTime { get; private set; }

        public void OnPongReveived()
        {
            LastContactTime = DateTime.UtcNow;
        }

        public override bool Equals(object obj)
        {
            if (obj is NodeBucketItem item && Node != null)
            {
                return Node.Id.Equals(item.Node?.Id);
            }

            return base.Equals(obj);
        }

        public override int GetHashCode()
        {
            return Node.GetHashCode();
        }
    }
}