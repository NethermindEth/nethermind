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

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;

namespace Nethermind.DataMarketplace.Core.Services
{
    public interface IDepositService
    {
        ulong GasLimit { get; }
        Task<Keccak?> MakeDepositAsync(Address onBehalfOf, Deposit deposit, UInt256 gasPrice);
        Task<uint> VerifyDepositAsync(Address onBehalfOf, Keccak depositId);
        Task<uint> VerifyDepositAsync(Address onBehalfOf, Keccak depositId, long blockNumber);
        Task<UInt256> ReadDepositBalanceAsync(Address onBehalfOf, Keccak depositId);
        Task ValidateContractAddressAsync(Address contractAddress);
    }
}