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

using System;

namespace Nethermind.Core
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
                case EthereumNetwork.Olympic:
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