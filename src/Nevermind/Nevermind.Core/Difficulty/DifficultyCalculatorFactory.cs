using System;
using System.Collections.Concurrent;

namespace Nevermind.Core.Difficulty
{
    public class DifficultyCalculatorFactory
    {
        private static readonly ConcurrentDictionary<EthereumNetwork, IDifficultyCalculator> Calculators
            = new ConcurrentDictionary<EthereumNetwork, IDifficultyCalculator>();

        public IDifficultyCalculator GetCalculator(EthereumNetwork ethereumNetwork)
        {
            switch (ethereumNetwork)
            {
                case EthereumNetwork.Main:
                    return Calculators.GetOrAdd(EthereumNetwork.Main, n => new MainNetworkDifficultyCalculator());
                case EthereumNetwork.Frontier:
                    return Calculators.GetOrAdd(EthereumNetwork.Frontier, n => new FrontierDifficultyCalculator());
                case EthereumNetwork.Homestead:
                    return Calculators.GetOrAdd(EthereumNetwork.Homestead, n => new HomesteadDifficultyCalculator());
                case EthereumNetwork.Metropolis:
                    throw new NotImplementedException();
                case EthereumNetwork.Serenity:
                    throw new NotImplementedException();
                case EthereumNetwork.Ropsten:
                    return Calculators.GetOrAdd(EthereumNetwork.Ropsten, n => new RopstenDifficultyCalculator());
                case EthereumNetwork.Morden:
                    return Calculators.GetOrAdd(EthereumNetwork.Morden, n => new MordenDifficultyCalculator());
                case EthereumNetwork.Olympic:
                    return Calculators.GetOrAdd(EthereumNetwork.Olympic, n => new OlimpicDifficultyCalculator());
                default:
                    throw new NotImplementedException();
            }
        }
    }
}