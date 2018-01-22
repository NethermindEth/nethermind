namespace Nevermind.Discovery.RoutingTable
{
    public interface INodeTable
    {
        Node MasterNode { get; }
        NodeBucket[] Buckets { get; }
        NodeAddResult AddNode(Node node);
        void DeleteNode(Node node);

        /// <summary>
        /// GetClosestNodesToMasterNode
        /// </summary>
        Node[] GetClosestNodes();
    }
}