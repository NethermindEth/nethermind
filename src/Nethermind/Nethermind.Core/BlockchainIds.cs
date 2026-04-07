// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core
{
    /// <summary>
    /// https://ethereum.stackexchange.com/questions/17051/how-to-select-a-network-id-or-is-there-a-list-of-network-ids/17101#17101
    /// 0: Olympic, Ethereum public pre-release PoW testnet
    /// 1: Expanse, an alternative Ethereum implementation, chain ID 2
    /// 2: Morden Classic, the public Ethereum Classic PoW testnet
    /// 60: GoChain, the GoChain networks mainnet
    /// 99: Core, the public POA Network main network
    /// 100: Gnosis, the public Gnosis main network
    /// 246: EnergyWeb, the public Energyweb main network
    /// 73799: Volta, the public Volta testnet
    /// 31337: GoChain testnet, the GoChain networks public testnet
    /// </summary>
    public static class BlockchainIds
    {
        public const ulong Olympic = 0;
        public const ulong Mainnet = 1;
        public const ulong Morden = 2;
        public const ulong RootstockMainnet = 30;
        public const ulong RootstockTestnet = 31;
        public const ulong Kovan = 42;
        public const ulong EthereumClassicMainnet = 61;
        public const ulong EthereumClassicTestnet = 62;
        public const ulong EnergyWeb = 246;
        public const ulong DefaultGethPrivateChain = 1337;
        public const ulong Gnosis = 100;
        public const ulong PoaCore = 99;
        public const ulong Chiado = 10200;
        public const ulong Volta = 73799;
        public const ulong Sepolia = 11155111;
        public const ulong Holesky = 17000;
        public const ulong Hoodi = 560048;

        // A generic network that does not exist and id is not claimed by any other actual network
        public const ulong GenericNonRealNetwork = 9999;

        public static string GetBlockchainName(ulong networkId)
        {
            return networkId switch
            {
                Olympic => nameof(Olympic),
                Mainnet => nameof(Mainnet),
                Morden => nameof(Morden),
                RootstockMainnet => nameof(RootstockMainnet),
                RootstockTestnet => nameof(RootstockTestnet),
                EthereumClassicMainnet => nameof(EthereumClassicMainnet),
                EthereumClassicTestnet => nameof(EthereumClassicTestnet),
                DefaultGethPrivateChain => nameof(DefaultGethPrivateChain),
                Gnosis => nameof(Gnosis),
                PoaCore => nameof(PoaCore),
                Chiado => nameof(Chiado),
                Volta => nameof(Volta),
                Sepolia => nameof(Sepolia),
                Holesky => nameof(Holesky),
                Hoodi => nameof(Hoodi),
                _ => networkId.ToString()
            };
        }
    }

    public static class TestBlockchainIds
    {
        public const ulong NetworkId = 4261;
        public const ulong ChainId = 1;
    }
}
