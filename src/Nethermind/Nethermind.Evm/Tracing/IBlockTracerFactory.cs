namespace Nethermind.Evm.Tracing
{
    public interface IBlockTracerFactory
    {
        public IBlockTracer Create();
    }
}
