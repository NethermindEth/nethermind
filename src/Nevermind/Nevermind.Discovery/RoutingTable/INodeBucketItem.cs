using System;

namespace Nevermind.Discovery.RoutingTable
{
    public interface INodeBucketItem
    {
        Node Node { get; }
        DateTime LastContactTime { get; }
        void OnPongReveived();
    }
}