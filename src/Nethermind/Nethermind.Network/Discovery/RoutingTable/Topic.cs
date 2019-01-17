namespace Nethermind.Network.Discovery.RoutingTable
{
    public class Topic
    {
        public string Name { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }
}   