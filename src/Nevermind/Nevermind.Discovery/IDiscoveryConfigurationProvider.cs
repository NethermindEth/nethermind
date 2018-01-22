namespace Nevermind.Discovery
{
    public interface IDiscoveryConfigurationProvider
    {
        /// <summary>
        /// Kademlia - k
        /// </summary>
        int BucketSize { get; }

        /// <summary>
        /// Buckets count
        /// </summary>
        int BucketsCount { get; }

        /// <summary>
        /// Kademlia - alpha
        /// </summary>
        int Concurrency { get; }

        /// <summary>
        /// Kademlia - b
        /// </summary>
        int BitsPerHop { get; }

        /// <summary>
        /// Current Node host
        /// </summary>
        string MasterHost { get; }

        /// <summary>
        /// Current Node port
        /// </summary>
        int MasterPort { get; }

        /// <summary>
        /// Max Discovery Rounds
        /// </summary>
        int MaxDiscoveryRounds { get; }

        /// <summary>
        /// Eviction check interval in ms
        /// </summary>
        int EvictionCheckInterval { get; }

        /// <summary>
        /// Request Timeout in ms
        /// </summary>
        int RequestTimeout { get; }
    }
}