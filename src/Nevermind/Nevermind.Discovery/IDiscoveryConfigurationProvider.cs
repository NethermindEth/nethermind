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
    }
}