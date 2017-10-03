using System;

namespace Nevermind.Core
{
    public static class EthereumNetworkExtensions
    {
        public static int GetNetworkId(this EthereumNetwork network)
        {
            switch (network)
            {
                case EthereumNetwork.Main:
                    return 1;
                case EthereumNetwork.Frontier:
                    return 1;
                case EthereumNetwork.Homestead:
                    return 1;
                case EthereumNetwork.Ropsten:
                    return 3;
                case EthereumNetwork.Morden:
                    return 2;
                case EthereumNetwork.Olimpic:
                    return 0;
                case EthereumNetwork.Kovan:
                    return 42;
                case EthereumNetwork.Rinkeby:
                    return 4;
                case EthereumNetwork.Metropolis:
                    return 1;
                case EthereumNetwork.Serenity:
                    return 1;
                default:
                    throw new NotImplementedException("Unknown Ethereum network");
            }
        }
    }
}