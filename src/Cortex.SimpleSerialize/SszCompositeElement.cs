using System.Collections.Generic;

namespace Cortex.SimpleSerialize
{
    public class SszCompositeElement : SszElement
    {
        private readonly IEnumerable<SszElement> _children;

        public SszCompositeElement(IEnumerable<SszElement> children)
        {
            _children = children;
        }

        public override SszElementType ElementType => throw new System.NotImplementedException();

        public IEnumerable<SszElement> GetChildren() => _children;
    }
}
