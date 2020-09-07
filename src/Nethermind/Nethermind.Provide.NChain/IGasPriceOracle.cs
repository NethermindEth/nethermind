//  Copyright (c) 2018 Demerzel Solutions Limited
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
// 

using System.Threading.Tasks;
using Nethermind.Int256;

namespace Nethermind.GasService
{
    public interface IGasPriceOracle
    {
        /// <summary>
        /// Provides the gas price to set on the transaction before signing.
        /// </summary>
        /// <param name="txGasLimit">Only used when service returns different prices for different tx sizes. Can be ignored by some of the service implementations.</param>
        /// <param name="priceType">Type of the gas price, can be ignored by some implementations.</param>
        /// <returns>Gas price in wei.</returns>
        Task<UInt256> GetGasPrice(ulong txGasLimit, GasPriceType priceType);
    }
}