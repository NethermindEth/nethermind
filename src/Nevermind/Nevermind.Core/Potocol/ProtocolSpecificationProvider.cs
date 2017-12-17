using System;
using System.Numerics;

namespace Nevermind.Core.Potocol
{
    public class ProtocolSpecificationProvider : IProtocolSpecificationProvider
    {
        public IEthereumRelease GetSpec(EthereumNetwork network, BigInteger blockNumber)
        {
            switch (network)
            {
                case EthereumNetwork.Main:
                    if (blockNumber < 1150000)
                    {
                        return Frontier.Instance;
                    }
                    else if (blockNumber < 2463000)
                    {
                        return Homestead.Instance;
                    }
                    else if (blockNumber < 2675000)
                    {
                        return TangerineWhistle.Instance;
                    }
                    else if (blockNumber < 4750000)
                    {
                        return SpuriousDragon.Instance;
                    }
                    else
                    {
                        return Byzantium.Instance;
                    }
                case EthereumNetwork.Ropsten:
                    if (blockNumber < 1150000)
                    {
                        return SpuriousDragon.Instance;
                    }
                    else
                    {
                        return Byzantium.Instance;
                    }
                case EthereumNetwork.Morden:
                    if (blockNumber < 494000)
                    {
                        return Frontier.Instance;
                    }
                    else if (blockNumber < 0) // ???
                    {
                        return Homestead.Instance;
                    }
                    else if (blockNumber < 1885000) // ???
                    {
                        return TangerineWhistle.Instance;
                    }
                    else
                    {
                        return SpuriousDragon.Instance;
                    }
                case EthereumNetwork.Frontier:
                    return Frontier.Instance;
                case EthereumNetwork.Homestead:
                    return Homestead.Instance;
                case EthereumNetwork.SpuriousDragon:
                    return SpuriousDragon.Instance;
                case EthereumNetwork.TangerineWhistle:
                    return TangerineWhistle.Instance;
                case EthereumNetwork.Byzantium:
                    return Byzantium.Instance;
                default:
                    throw new NotImplementedException();
            }
        }
    }
}