// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core
{
    /// <summary>
    /// https://ethereum.stackexchange.com/questions/17051/how-to-select-a-network-id-or-is-there-a-list-of-network-ids/17101#17101
    /// 0: Olympic, Ethereum public pre-release PoW testnet
    /// 1: Frontier, Homestead, Metropolis, the Ethereum public PoW main network
    /// 1: Classic, the (un)forked public Ethereum Classic PoW main network, chain ID 61
    /// 1: Expanse, an alternative Ethereum implementation, chain ID 2
    /// 2: Morden Classic, the public Ethereum Classic PoW testnet
    /// 3: Ropsten, the public cross-client Ethereum PoS testnet
    /// 4: Rinkeby, the public Geth-only PoA testnet
    /// 5: Goerli, the public cross-client PoA testnet
    /// 6: Kotti Classic, the public cross-client PoA testnet for Classic
    /// 8: Ubiq, the public Gubiq main network with flux difficulty chain ID 8
    /// 42: Kovan, the public Parity-only PoA testnet
    /// 60: GoChain, the GoChain networks mainnet
    /// 77: Sokol, the public POA Network testnet
    /// 99: Core, the public POA Network main network
    /// 100: xDai, the public MakerDAO/POA Network main network
    /// 246: EnergyWeb, the public Energyweb main network
    /// 73799: Volta, the public Volta testnet
    /// 31337: GoChain testnet, the GoChain networks public testnet
    /// 401697: Tobalaba, the public Energy Web Foundation testnet
    /// 7762959: Musicoin, the music blockchain
    /// 61717561: Aquachain, ASIC resistant chain
    /// </summary>
    public static class BlockchainIds
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
        public const int xDai = 100;
        public const int PoaCore = 99;
        public const int Volta = 73799;
        public const int Sepolia = 11155111;

        public static string GetBlockchainName(ulong networkId)
        {
            return networkId switch
            {
                Olympic => nameof(Olympic),
                Mainnet => nameof(Mainnet),
                Morden => nameof(Morden),
                Ropsten => nameof(Ropsten),
                Rinkeby => nameof(Rinkeby),
                Goerli => nameof(Goerli),
                RootstockMainnet => nameof(RootstockMainnet),
                RootstockTestnet => nameof(RootstockTestnet),
                Kovan => nameof(Kovan),
                EthereumClassicMainnet => nameof(EthereumClassicMainnet),
                EthereumClassicTestnet => nameof(EthereumClassicTestnet),
                DefaultGethPrivateChain => nameof(DefaultGethPrivateChain),
                Stureby => nameof(Stureby),
                xDai => nameof(xDai),
                PoaCore => nameof(PoaCore),
                Volta => nameof(Volta),
                Sepolia => nameof(Sepolia),
                _ => networkId.ToString()
            };
        }
    }

    public static class TestBlockchainIds
    {
        public const int NetworkId = 4261;
        public const int ChainId = 1;
    }
}
