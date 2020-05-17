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
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Serialization.Json.Abi;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public partial class TransactionPermissionContract
    {
        private class V3 : ITransactionPermissionVersionedContract, IVersionContract
        {
            private static readonly UInt256 Three = 3;

            public AbiDefinition Definition { get; } = new AbiDefinitionParser().Parse<V3>();
            public ConstantContract Constant { get; }

            public V3(ConstantContract constant)
            {
                Constant = constant;
            }
            
            public (TxPermissions Permissions, bool ShouldCache) AllowedTxTypes(BlockHeader parentHeader, Transaction tx) =>
                // _sender Transaction sender address.
                // _to Transaction recipient address. If creating a contract, the `_to` address is zero.
                // _value Transaction amount in wei.
                // _gasPrice Gas price in wei for the transaction.
                // _data Transaction data.
                Constant.Call<TxPermissions, bool>(
                    parentHeader, 
                    Definition.GetFunction(nameof(AllowedTxTypes)), 
                    Address.Zero, 
                    tx.SenderAddress, tx.To ?? Address.Zero, tx.Value, tx.GasPrice, tx.Data ?? tx.Init ?? Bytes.Empty);

            public UInt256 Version => Three;
        }
    }
}