using System.Numerics;

namespace Nevermind.Core.Potocol
{
    public interface IProtocolSpecificationProvider
    {
        IProtocolSpecification GetSpec(EthereumNetwork network, BigInteger blockNumber);
    }
}