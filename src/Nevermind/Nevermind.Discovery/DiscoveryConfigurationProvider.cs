namespace Nevermind.Discovery
{
    public class DiscoveryConfigurationProvider : IDiscoveryConfigurationProvider
    {
        public int BucketSize => 16;
        public int BucketsCount => 256;
        public int Concurrency => 3;
        public int BitsPerHop => 8;
        public string MasterHost => "localhost";
        public int MasterPort => 10000;
    }
}