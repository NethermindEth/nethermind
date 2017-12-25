namespace Nevermind.Core
{
    public enum EthereumNetwork
    {
        Main,
        Frontier, // launched 30/07/2015

        /// <summary>
        ///     https://github.com/ethereum/EIPs/blob/master/EIPS/eip-606.md
        /// </summary>
        Homestead, // launched 14/03/2016 Block >= 1,150,000 on MainNet Block >= 494,000 on Morden

        /// <summary>
        ///     https://github.com/ethereum/EIPs/blob/master/EIPS/eip-608.md
        /// </summary>
        TangerineWhistle, // launched 23/04/2017 ? Block >= 2,463,000 on MainNet

        /// <summary>
        ///     https://github.com/ethereum/EIPs/blob/master/EIPS/eip-607.md
        /// </summary>
        SpuriousDragon, // launched 23/04/2017 ? Block >= 2,675,000 on MainNet Block >= 1,885,000 on Morden
        Ropsten,
        Morden,
        Olympic, // launched May 2015
        Kovan,
        Rinkeby,
        Metropolis,
        Byzantium, // launched 17/10/2017 ?
        Serenity
    }
}