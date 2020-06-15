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
using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public interface ITransactionPermissionContract : IVersionedContract
    {
        /// <summary>
        /// Returns the contract version number needed for node's engine.
        /// </summary>
        UInt256 Version { get; }

        /// <summary>
        /// Defines the allowed transaction types which may be initiated by the specified sender with
        /// the specified gas price and data. Used by node's engine each time a transaction is about to be
        /// included into a block.
        /// </summary>
        /// <param name="parentHeader"></param>
        /// <param name="tx"></param>
        /// <returns><see cref="TxPermissions"/>Set of allowed transactions types and <see cref="bool"/> If `true` is returned, the same permissions will be applied from the same sender without calling this contract again.</returns>
        (TxPermissions Permissions, bool ShouldCache) AllowedTxTypes(BlockHeader parentHeader, Transaction tx);
        
        [Flags]
        public enum TxPermissions : uint
        {
            /// <summary>
            /// No permissions
            /// </summary>
            None = 0x0,

            /// <summary>
            /// 0x01 - basic transaction (e.g. ether transferring to user wallet)
            /// </summary>
            Basic = 0b00000001,

            /// <summary>
            /// 0x02 - contract call
            /// </summary>
            Call = 0b00000010,

            /// <summary>
            /// 0x04 - contract creation
            /// </summary>
            Create = 0b00000100,

            /// <summary>
            /// 0x08 - private transaction
            /// </summary>
            Private = 0b00001000,

            All = 0xffffffff,
        }
    }

    public abstract class TransactionPermissionContract : Contract, ITransactionPermissionContract
    {
        public virtual UInt256 ContractVersion(BlockHeader blockHeader)
        {
            try
            {
                return Constant.Call<UInt256>(blockHeader, nameof(ContractVersion), Address.Zero);
            }
            catch (Exception)
            {
                return UInt256.One;;
            }
        }

        /// <summary>
        /// Returns the contract version number needed for node's engine.
        /// </summary>
        public abstract UInt256 Version { get; }

        /// <summary>
        /// Defines the allowed transaction types which may be initiated by the specified sender with
        /// the specified gas price and data. Used by node's engine each time a transaction is about to be
        /// included into a block.
        /// </summary>
        /// <param name="parentHeader"></param>
        /// <param name="tx"></param>
        /// <returns><see cref="ITransactionPermissionContract.TxPermissions"/>Set of allowed transactions types and <see cref="bool"/> If `true` is returned, the same permissions will be applied from the same sender without calling this contract again.</returns>
        public abstract (ITransactionPermissionContract.TxPermissions Permissions, bool ShouldCache) AllowedTxTypes(BlockHeader parentHeader, Transaction tx);

        protected ConstantContract Constant { get; }

        protected TransactionPermissionContract(
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTransactionProcessorSource readOnlyTransactionProcessorSource)
            : base(abiEncoder, contractAddress)
        {
            Constant = GetConstant(readOnlyTransactionProcessorSource);
        }
    }
}
