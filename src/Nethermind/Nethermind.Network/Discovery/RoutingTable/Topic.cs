using System;
using System.Collections.Generic;
using System.Text;

namespace Nethermind.Network.Discovery.RoutingTable
{
    public class Topic
    {
        public string Name { get; set; }

        public override string ToString()
        {
            return Name;
        }

        public Topic(string name)
        {
            Name = name;
        }
    }
}
