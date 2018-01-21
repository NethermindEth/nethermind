namespace Nevermind.Discovery.RoutingTable
{
    public interface INodeDistanceCalculator
    {
        int CalculateDistance(byte[] sourceId, byte[] targetId);
    }
}