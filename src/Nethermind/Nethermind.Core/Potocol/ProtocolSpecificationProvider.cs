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
using System.Numerics;

namespace Nethermind.Core.Potocol
{
    public class ProtocolSpecificationProvider : IProtocolSpecificationProvider
    {
        public IEthereumRelease GetSpec(EthereumNetwork network, BigInteger blockNumber)
        {
            switch (network)
            {
                case EthereumNetwork.Main:
                    if (blockNumber < 1150000)
                    {
                        return Frontier.Instance;
                    }
                    else if (blockNumber < 2463000)
                    {
                        return Homestead.Instance;
                    }
                    else if (blockNumber < 2675000)
                    {
                        return TangerineWhistle.Instance;
                    }
                    else if (blockNumber < 4750000)
                    {
                        return SpuriousDragon.Instance;
                    }
                    else
                    {
                        return Byzantium.Instance;
                    }
                case EthereumNetwork.Ropsten:
                    if (blockNumber < 1150000)
                    {
                        return SpuriousDragon.Instance;
                    }
                    else
                    {
                        return Byzantium.Instance;
                    }
                case EthereumNetwork.Morden:
                    if (blockNumber < 494000)
                    {
                        return Frontier.Instance;
                    }
                    else if (blockNumber < 0) // ???
                    {
                        return Homestead.Instance;
                    }
                    else if (blockNumber < 1885000) // ???
                    {
                        return TangerineWhistle.Instance;
                    }
                    else
                    {
                        return SpuriousDragon.Instance;
                    }
                case EthereumNetwork.Frontier:
                    return Frontier.Instance;
                case EthereumNetwork.Homestead:
                    return Homestead.Instance;
                case EthereumNetwork.SpuriousDragon:
                    return SpuriousDragon.Instance;
                case EthereumNetwork.TangerineWhistle:
                    return TangerineWhistle.Instance;
                case EthereumNetwork.Byzantium:
                    return Byzantium.Instance;
                default:
                    throw new NotImplementedException();
            }
        }
    }
}