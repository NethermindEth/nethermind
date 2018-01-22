namespace Nevermind.Discovery.RoutingTable
{
    public class NodeDistanceCalculator : INodeDistanceCalculator
    {
        private readonly IDiscoveryConfigurationProvider _configurationProvider;
        private readonly int _maxDistance;
        private readonly int _bitsPerHoop;

        public NodeDistanceCalculator(IDiscoveryConfigurationProvider configurationProvider)
        {
            _configurationProvider = configurationProvider;
            _maxDistance = _configurationProvider.BucketsCount;
            _bitsPerHoop = _configurationProvider.BitsPerHop;
        }

        public int CalculateDistance(byte[] sourceId, byte[] destinationId)
        {
            var hash = new byte[destinationId.Length < sourceId.Length ? destinationId.Length : sourceId.Length];

            for (var i = 0; i < hash.Length; i++)
            {
                hash[i] = (byte)(destinationId[i] ^ sourceId[i]);
            }

            var distance = _maxDistance;

            for (var i = 0; i < hash.Length; i++)
            {
                var b = hash[i];
                if (b == 0)
                {
                    distance -= _bitsPerHoop;
                }
                else
                {
                    var count = 0;
                    for (var j = _bitsPerHoop - 1; j >= 0; j--)
                    {
                        //why not b[j] == 0
                        if ((b & (1 << j)) == 0)
                        {
                            count++;
                        }
                        else
                        {
                            break;
                        }
                    }
                    distance -= count;
                    break;
                }
            }
            return distance;
        }
    }
}