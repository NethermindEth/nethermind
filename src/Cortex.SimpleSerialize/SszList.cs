using System.Collections.Generic;
using System.Linq;

namespace Cortex.SimpleSerialize
{
    public class SszList : SszComposite
    {
        private readonly IEnumerable<SszComposite> _values;

        public SszList(IEnumerable<SszComposite> values, int limit)
        {
            // Chunk count for list of composite is N (we merkleize the hash root of each)
            ByteLimit = limit * SszTree.BytesPerChunk;
            _values = values;
        }

        public int ByteLimit { get; }
        public override SszElementType ElementType => SszElementType.List;

        public int Length
        {
            get
            {
                return _values.Count();
            }
        }

        public IEnumerable<SszComposite> GetValues() => _values;
    }
}
