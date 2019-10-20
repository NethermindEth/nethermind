using System.Collections.Generic;

namespace Cortex.SimpleSerialize
{
    public class SszContainer : SszComposite
    {
        private readonly IEnumerable<SszElement?> _values;

        public SszContainer(IEnumerable<SszElement?> values)
        {
            _values = values;
        }

        public override SszElementType ElementType => SszElementType.Container;

        public IEnumerable<SszElement?> GetValues() => _values;
    }
}
