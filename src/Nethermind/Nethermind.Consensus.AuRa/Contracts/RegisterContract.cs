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
// 

using System;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public interface IRegisterContract
    {
        bool TryGetAddress(BlockHeader header, string key, out Address address);
        Address GetAddress(BlockHeader header, string key);
    }

    /// <summary>
    /// Contract for registry of values (dictionary) on chain
    /// </summary>
    public class RegisterContract : Contract, IRegisterContract
    {
        private static Address MissingAddress = Address.Zero;
        private static readonly object[] MissingGetAddressResult = {MissingAddress};
        
        /// <summary>
        /// Category of domain name service addresses
        /// </summary>
        private const string DnsAddressRecord = "A";
        private IConstantContract Constant { get; }
        
        public RegisterContract(
            IAbiEncoder abiEncoder, 
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource) 
            : base(abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)))
        {
            Constant = GetConstant(readOnlyTxProcessorSource);
        }

        public bool TryGetAddress(BlockHeader header, string key, out Address address)
        {
            try
            {
                address = GetAddress(header, key);
                return !ReferenceEquals(address, MissingAddress);
            }
            catch (AbiException)
            {
                address = MissingAddress;
                return false;
            }
        }

        public Address GetAddress(BlockHeader header, string key) =>
            // 2 arguments: name and key (category)
            Constant.Call<Address>(
                new CallInfo(header, nameof(GetAddress), Address.Zero, Keccak.Compute(key).Bytes, DnsAddressRecord) {MissingContractResult = MissingGetAddressResult});
    }
}
