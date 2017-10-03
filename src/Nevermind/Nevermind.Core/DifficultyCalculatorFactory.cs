using System;

namespace Nevermind.Core
{
    public class DifficultyCalculatorFactory
    {
        public IDifficultyCalculator GetCalculator(EthereumNetwork ethereumNetwork)
        {
            switch (ethereumNetwork)
            {
                case EthereumNetwork.Main:
                    return new MainNetworkDifficultyCalculator();
                case EthereumNetwork.Frontier:
                    return new FrontierDifficultyCalculator();
                case EthereumNetwork.Homestead:
                    return new HomesteadDifficultyCalculator();
                case EthereumNetwork.Metropolis:
                    throw new NotImplementedException();
                case EthereumNetwork.Serenity:
                    throw new NotImplementedException();
                case EthereumNetwork.Ropsten:
                    return new RopstenDifficultyCalculator();
                case EthereumNetwork.Morden:
                    return new MordenDifficultyCalculator();
                case EthereumNetwork.Olimpic:
                    return new OlimpicDifficultyCalculator();
                default:
                    throw new NotImplementedException();
            }
        }
    }
}