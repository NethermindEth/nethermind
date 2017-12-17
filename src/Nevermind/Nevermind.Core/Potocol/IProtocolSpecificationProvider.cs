using System.Numerics;

namespace Nevermind.Core.Potocol
{
    public interface IProtocolSpecificationProvider
    {
        IEthereumRelease GetSpec(EthereumNetwork network, BigInteger blockNumber);
    }
}