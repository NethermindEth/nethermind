/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

namespace Nethermind.Core
{
    public static class ChainId
    {
        public const int Olympic = 0;
        public const int MainNet = 1;
        public const int Morden = 2;
        public const int Ropsten = 3;
        public const int Rinkeby = 4;
        public const int Goerli = 0x188c;
        public const int RootstockMainnet = 30;
        public const int RootstockTestnet = 31;
        public const int Kovan = 42;
        public const int EthereumClassicMainnet = 61;
        public const int EthereumClassicTestnet = 62;
        public const int DefaultGethPrivateChain = 1337;

        public static string GetChainName(int chaninId)
        {
            switch (chaninId)
            {
                case Olympic:
                    return "Olympic";
                case MainNet:
                    return "MainNet";
                case Morden:
                    return "Morden";
                case Ropsten:
                    return "Ropsten";
                case Rinkeby:
                    return "Rinkeby";
                case Goerli:
                    return "Goerli";
                case RootstockMainnet:
                    return "RootstockMainnet";
                case RootstockTestnet:
                    return "RootstockTestnet";
                case Kovan:
                    return "Kovan";
                case EthereumClassicMainnet:
                    return "EthereumClassicMainnet";
                case EthereumClassicTestnet:
                    return "EthereumClassicTestnet";
                case DefaultGethPrivateChain:
                    return "DefaultGethPrivateChain";
            }

            return chaninId.ToString();
        }
    }
}