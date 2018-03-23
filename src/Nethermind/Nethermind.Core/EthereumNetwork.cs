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
    /// <summary>
    /// There are 'fake' single release networks here for testing (like Byzantium) and also networks like Main or Morden which have multiple releases over time
    /// </summary>
    public enum EthereumNetwork
    {
        Main,
        Frontier, // launched 30/07/2015

        /// <summary>
        ///     https://github.com/ethereum/EIPs/blob/master/EIPS/eip-606.md
        /// </summary>
        Homestead, // launched 14/03/2016 Block >= 1,150,000 on MainNet Block >= 494,000 on Morden
        Dao, // launched 20/07/2016 Block >= 1,920,000 on MainNet Block >= ? on Morden

        /// <summary>
        ///     https://github.com/ethereum/EIPs/blob/master/EIPS/eip-608.md
        /// </summary>
        TangerineWhistle, // launched 18/10/2016 on MainNet Block >= 2,463,000 on MainNet

        /// <summary>
        ///     https://github.com/ethereum/EIPs/blob/master/EIPS/eip-607.md
        /// </summary>
        SpuriousDragon, // launched 22/11/2016 on MainNet Block >= 2,675,000 on MainNet Block >= 1,885,000 on Morden
        Ropsten,
        Morden,
        Olympic, // launched May 2015
        Kovan,
        Rinkeby,
        Metropolis,
        Byzantium, // launched 16/10/2017 on MainNet Block > 4730000
        Serenity
    }
}