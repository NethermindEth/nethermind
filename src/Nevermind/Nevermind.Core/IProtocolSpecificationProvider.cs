using System.Numerics;

namespace Nevermind.Core
{
    public interface IProtocolSpecificationProvider
    {
        IProtocolSpecification GetSpec(EthereumNetwork network, BigInteger blockNumber);
    }
}