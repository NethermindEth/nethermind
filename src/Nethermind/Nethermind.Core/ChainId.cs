//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

namespace Nethermind.Core
{
    /// <summary>
    /// https://ethereum.stackexchange.com/questions/17051/how-to-select-a-network-id-or-is-there-a-list-of-network-ids/17101#17101
    /// 0: Olympic, Ethereum public pre-release PoW testnet
    /// 1: Frontier, Homestead, Metropolis, the Ethereum public PoW main network
    /// 1: Classic, the (un)forked public Ethereum Classic PoW main network, chain ID 61
    /// 1: Expanse, an alternative Ethereum implementation, chain ID 2
    /// 2: Morden Classic, the public Ethereum Classic PoW testnet
    /// 3: Ropsten, the public cross-client Ethereum PoW testnet
    /// 4: Rinkeby, the public Geth-only PoA testnet
    /// 5: Goerli, the public cross-client PoA testnet
    /// 6: Kotti Classic, the public cross-client PoA testnet for Classic
    /// 8: Ubiq, the public Gubiq main network with flux difficulty chain ID 8
    /// 42: Kovan, the public Parity-only PoA testnet
    /// 60: GoChain, the GoChain networks mainnet
    /// 77: Sokol, the public POA Network testnet
    /// 99: Core, the public POA Network main network
    /// 100: xDai, the public MakerDAO/POA Network main network
    /// 31337: GoChain testnet, the GoChain networks public testnet
    /// 401697: Tobalaba, the public Energy Web Foundation testnet
    /// 7762959: Musicoin, the music blockchain
    /// 61717561: Aquachain, ASIC resistant chain
    /// </summary>
    public static class ChainId
    {
        public const int Olympic = 0;
        public const int Mainnet = 1;
        public const int Morden = 2;
        public const int Ropsten = 3;
        public const int Rinkeby = 4;
        public const int Goerli = 5;
        public const int RootstockMainnet = 30;
        public const int RootstockTestnet = 31;
        public const int Kovan = 42;
        public const int EthereumClassicMainnet = 61;
        public const int EthereumClassicTestnet = 62;
        public const int EnergyWeb = 246;
        public const int DefaultGethPrivateChain = 1337;
        public const int Stureby = 314158;

        public static string GetChainName(ulong chainId)
        {
            return chainId switch
            {
                Olympic => "Olympic",
                Mainnet => "Mainnet",
                Morden => "Morden",
                Ropsten => "Ropsten",
                Rinkeby => "Rinkeby",
                Goerli => "Goerli",
                RootstockMainnet => "RootstockMainnet",
                RootstockTestnet => "RootstockTestnet",
                Kovan => "Kovan",
                EthereumClassicMainnet => "EthereumClassicMainnet",
                EthereumClassicTestnet => "EthereumClassicTestnet",
                DefaultGethPrivateChain => "DefaultGethPrivateChain",
                Stureby => "Stureby",
                _ => chainId.ToString()
            };
        }
    }
}
