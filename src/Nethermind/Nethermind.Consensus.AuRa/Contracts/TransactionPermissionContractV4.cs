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
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.Contracts
{
    /// <summary>Version four of the contract. Created to adjust EIP1559 changes.</summary>
    public sealed class TransactionPermissionContractV4 : TransactionPermissionContract
    {
        private readonly ISpecProvider _specProvider;
        private static readonly UInt256 Four = 4;

        public TransactionPermissionContractV4(
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            ISpecProvider specProvider)
            : base(abiEncoder, contractAddress, readOnlyTxProcessorSource)
        {
            _specProvider = specProvider;
        }


        protected override object[] GetAllowedTxTypesParameters(Transaction tx, BlockHeader parentHeader)
        {
            // _sender Transaction sender address.
            // _to Transaction recipient address. If creating a contract, the `_to` address is zero.
            // _value Transaction amount in wei.
            // _maxFeePerGas instead of the legacy _gasPrice
            // _maxInclusionFeePerGas instead of the legacy _gasPrice
            // _gasLimit
            // _data Transaction data.
            
            return new object[]
            {
                tx.SenderAddress, tx.To ?? Address.Zero, tx.Value, tx.MaxFeePerGas, tx.MaxPriorityFeePerGas, tx.GasLimit, tx.Data ?? Array.Empty<byte>()
            };
        }

        public override UInt256 Version => Four;
    }
}
