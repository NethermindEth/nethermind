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

using System;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class TransactionPermissionContractV2 : TransactionPermissionContract
    {
        protected override AbiDefinition AbiDefinition { get; }
            = new AbiDefinitionParser().Parse<TransactionPermissionContractV2>();
        
        private static readonly UInt256 Two = 2;

        public TransactionPermissionContractV2(
            ITransactionProcessor transactionProcessor,
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTransactionProcessorSource readOnlyReadOnlyTransactionProcessorSource)
            : base(transactionProcessor, abiEncoder, contractAddress, readOnlyReadOnlyTransactionProcessorSource)
        {
        }

        public override (TxPermissions Permissions, bool ShouldCache) AllowedTxTypes(BlockHeader parentHeader, Transaction tx) => 
            Constant.Call<TxPermissions, bool>(parentHeader, nameof(AllowedTxTypes), Address.Zero, tx.SenderAddress, tx.To ?? Address.Zero, tx.Value);

        public override UInt256 Version => Two;
    }
}