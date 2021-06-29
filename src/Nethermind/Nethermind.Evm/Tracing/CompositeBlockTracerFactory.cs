using System.Collections.Immutable;
using System.Linq;

namespace Nethermind.Evm.Tracing
{
    public class CompositeBlockTracerFactory : IBlockTracerFactory
    {
        private ImmutableArray<IBlockTracerFactory> _childFactories = ImmutableArray<IBlockTracerFactory>.Empty;

        public IBlockTracer Create()
        {
            return _childFactories.Length == 0 ? NullBlockTracer.Instance : new CompositeBlockTracer(_childFactories.Select(factory => factory.Create()).ToArray());
        }

        public void AddChildFactory(IBlockTracerFactory factoryToAdd)
        {
            _childFactories = _childFactories.Add(factoryToAdd);
        }

        public void RemoveChildFactory(IBlockTracerFactory factoryToRemove)
        {
            _childFactories = _childFactories.Remove(factoryToRemove);
        }
    }
}
