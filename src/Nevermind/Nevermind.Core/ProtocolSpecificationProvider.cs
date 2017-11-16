using System;
using System.Numerics;

namespace Nevermind.Core
{
    public class ProtocolSpecificationProvider : IProtocolSpecificationProvider
    {
        public IProtocolSpecification GetSpec(EthereumNetwork network, BigInteger blockNumber)
        {
            switch (network)
            {
                case EthereumNetwork.Main:
                    if (blockNumber < 1150000)
                    {
                        return new FrontierProtocolSpecification();
                    }
                    else if (blockNumber < 2463000)
                    {
                        return new HomesteadProtocolSpecification();
                    }
                    else if (blockNumber < 2675000)
                    {
                        return new TangerineWhistleProtocolSpecification();
                    }
                    else if (blockNumber < 4750000)
                    {
                        return new SpuriousDragonProtocolSpecification();
                    }
                    else
                    {
                        return new ByzantiumProtocolSpecification();
                    }
                case EthereumNetwork.Frontier:
                    return new FrontierProtocolSpecification();
                case EthereumNetwork.Homestead:
                    return new HomesteadProtocolSpecification();
                case EthereumNetwork.SpuriousDragon:
                    return new SpuriousDragonProtocolSpecification();
                case EthereumNetwork.TangerineWhistle:
                    return new TangerineWhistleProtocolSpecification();
                case EthereumNetwork.Byzantium:
                    return new ByzantiumProtocolSpecification();
                default:
                    throw new NotImplementedException();
            }
        }
    }
}