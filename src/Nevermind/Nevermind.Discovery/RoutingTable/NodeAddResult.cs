namespace Nevermind.Discovery.RoutingTable
{
    public class NodeAddResult
    {
        public NodeAddResultType ResultType { get; set; }
        public NodeBucketItem EvictionCandidate { get; set; }

        public static NodeAddResult Added() { return new NodeAddResult{ResultType = NodeAddResultType.Added}; }
        public static NodeAddResult Full(NodeBucketItem evictionCandidate) { return new NodeAddResult { ResultType = NodeAddResultType.Full, EvictionCandidate = evictionCandidate }; }
    }
}