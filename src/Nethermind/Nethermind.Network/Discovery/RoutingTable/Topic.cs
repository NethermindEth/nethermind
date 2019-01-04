//TODO: Change this to be in Nethermind.Stats.Model

namespace Nethermind.Network.Discovery.RoutingTable
{
    public class Topic
    {
        public string Name { get; set; }

        public Topic(string topic) {
            Name = topic;
        }

        public override string ToString()
        {
            return Name;
        }
    }
} 