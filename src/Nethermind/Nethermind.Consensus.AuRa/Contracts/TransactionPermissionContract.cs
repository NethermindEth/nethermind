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
using System.Linq;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public partial class TransactionPermissionContract : Contract, IActivatedAtBlock
    {
        private readonly IDictionary<UInt256, ITransactionPermissionVersionedContract> _versionedContracts;
        private readonly IVersionContract _versionContract;

        public TransactionPermissionContract(ITransactionProcessor transactionProcessor,
            IAbiEncoder abiEncoder,
            Address contractAddress,
            long activationBlock,
            IReadOnlyTransactionProcessorSource readOnlyReadOnlyTransactionProcessorSource) 
            : base(transactionProcessor, abiEncoder, contractAddress)
        {
            ActivationBlock = activationBlock;
            var constantContract = GetConstant(readOnlyReadOnlyTransactionProcessorSource);
            _versionedContracts = GetContracts(constantContract).ToDictionary(c => c.Version);
            _versionContract = (IVersionContract) _versionedContracts.Values.First(c => c is IVersionContract);
        }

        private IEnumerable<ITransactionPermissionVersionedContract> GetContracts(ConstantContract constant)
        {
            yield return new V1(constant);
            yield return new V2(constant);
            yield return new V3(constant);
        }

        public long ActivationBlock { get; }
        
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
        
        public ITransactionPermissionVersionedContract GetVersionedContract(BlockHeader parentHeader)
        {
            this.ActivationCheck(parentHeader);
            
            try
            {
                UInt256 version = _versionContract.ContractVersion(parentHeader);
                return GetVersionedContract(version);
            }
            catch (AuRaException)
            {
                return _versionedContracts[UInt256.One];
            }
        }
        
        public ITransactionPermissionVersionedContract GetVersionedContract(UInt256 version) => _versionedContracts.TryGetValue(version, out var contract) ? contract : null;

        public interface ITransactionPermissionVersionedContract
        {
            /// <summary>
            /// Defines the allowed transaction types which may be initiated by the specified sender with
            /// the specified gas price and data. Used by node's engine each time a transaction is about to be
            /// included into a block.
            /// </summary>
            /// <param name="parentHeader"></param>
            /// <param name="tx"></param>
            /// <returns><see cref="TxPermissions"/>Set of allowed transactions types and <see cref="bool"/> If `true` is returned, the same permissions will be applied from the same sender without calling this contract again.</returns>
            (TxPermissions Permissions, bool ShouldCache) AllowedTxTypes(BlockHeader parentHeader, Transaction tx);
            
            /// <summary>
            /// Returns the contract's version number needed for node's engine.
            /// </summary>
            UInt256 Version { get; }
        }

        private interface IVersionContract
        {
            AbiDefinition Definition { get; }
            ConstantContract Constant { get; }
            public UInt256 ContractVersion(BlockHeader blockHeader) => Constant.Call<UInt256>(blockHeader, Definition.GetFunction(nameof(ContractVersion)), Address.Zero);
        }
    }
}