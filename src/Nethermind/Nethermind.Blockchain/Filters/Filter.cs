using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Filters
{
    public class Filter : FilterBase
    {
        public Block FromBlock { get; set; }
        public Block ToBlock { get; set; }
        public FilterAddress Address { get; set; }
        public IEnumerable<FilterTopic> Topics { get; set; }
    }
}