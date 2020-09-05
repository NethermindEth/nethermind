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

using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public interface IRegisterContract
    {
        Address GetAddress(BlockHeader header, string key);
    }

    /// <summary>
    /// Contract for registry of values (dictionary) on chain
    /// </summary>
    public class RegisterContract : Contract, IRegisterContract
    {
        /// <summary>
        /// Category of domain name service addresses
        /// </summary>
        private const string DnsAddressRecord = "A";
        private ConstantContract Constant { get; }
        
        public RegisterContract(
            IAbiEncoder abiEncoder, 
            Address contractAddress,
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource) 
            : base(abiEncoder, contractAddress)
        {
            Constant = GetConstant(readOnlyTransactionProcessorSource);
        }

        public Address GetAddress(BlockHeader header, string key) => 
            Constant.Call<Address>(header, nameof(GetAddress), Address.Zero, Keccak.Compute(key).Bytes, DnsAddressRecord); // 2 arguments: name and key (category)
    }
}
