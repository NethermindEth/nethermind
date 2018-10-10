using System.Collections.Generic;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Blockchain.Filters
{
    public class Filter : FilterBase
    {
        //TODO: Number or type (latest etc.)
        public FilterBlock FromBlock { get; set; }
        public FilterBlock ToBlock { get; set; }
        public FilterAddress Address { get; set; }
        public IEnumerable<FilterData> Topics { get; set; }
    }
}