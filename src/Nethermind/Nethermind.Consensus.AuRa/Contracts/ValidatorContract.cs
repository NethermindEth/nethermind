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

using System;
using System.Collections.Generic;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.State;
using Nethermind.Blockchain.Find;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public partial interface IValidatorContract
    {
        /// <summary>
        /// Called when an initiated change reaches finality and is activated.
        /// Only valid when msg.sender == SUPER_USER (EIP96, 2**160 - 2)
        ///
        /// Also called when the contract is first enabled for consensus. In this case,
        /// the "change" finalized is the activation of the initial set.
        /// function finalizeChange();
        /// </summary>
        void FinalizeChange(BlockHeader blockHeader);

        /// <summary>
        /// Get current validator set (last enacted or initial if no changes ever made)
        /// function getValidators() constant returns (address[] _validators);
        /// </summary>
        Address[] GetValidators(BlockHeader parentHeader);

        /// <summary>
        /// Issue this log event to signal a desired change in validator set.
        /// This will not lead to a change in active validator set until
        /// finalizeChange is called.
        ///
        /// Only the last log event of any block can take effect.
        /// If a signal is issued while another is being finalized it may never
        /// take effect.
        ///
        /// _parent_hash here should be the parent block hash, or the
        /// signal will not be recognized.
        /// event InitiateChange(bytes32 indexed _parent_hash, address[] _new_set);
        /// </summary>
        bool CheckInitiateChangeEvent(BlockHeader blockHeader, TxReceipt[] receipts, out Address[] addresses);

        void EnsureSystemAccount();
    }
    
    public sealed partial class ValidatorContract : CallableContract, IValidatorContract
    {
        private readonly IStateProvider _stateProvider;
        private readonly ISigner _signer;

        private IConstantContract Constant { get; }

        public ValidatorContract(
            ITransactionProcessor transactionProcessor, 
            IAbiEncoder abiEncoder, 
            Address contractAddress, 
            IStateProvider stateProvider,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            ISigner signer) 
            : base(transactionProcessor, abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)))
        {
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
            Constant = GetConstant(readOnlyTxProcessorSource);
        }

        /// <summary>
        /// Called when an initiated change reaches finality and is activated.
        /// Only valid when msg.sender == SUPER_USER (EIP96, 2**160 - 2)
        ///
        /// Also called when the contract is first enabled for consensus. In this case,
        /// the "change" finalized is the activation of the initial set.
        /// function finalizeChange();
        /// </summary>
        public void FinalizeChange(BlockHeader blockHeader) => TryCall(blockHeader, nameof(FinalizeChange), Address.SystemUser, UnlimitedGas, out _);

        internal static readonly string GetValidatorsFunction = AbiDefinition.GetName(nameof(GetValidators));

        /// <summary>
        /// Get current validator set (last enacted or initial if no changes ever made)
        /// function getValidators() constant returns (address[] _validators);
        /// </summary>
        public Address[] GetValidators(BlockHeader parentHeader) => Constant.Call<Address[]>(parentHeader, nameof(GetValidators), Address.Zero);

        internal const string InitiateChange = nameof(InitiateChange);
        
        /// <summary>
        /// Issue this log event to signal a desired change in validator set.
        /// This will not lead to a change in active validator set until
        /// finalizeChange is called.
        ///
        /// Only the last log event of any block can take effect.
        /// If a signal is issued while another is being finalized it may never
        /// take effect.
        ///
        /// _parent_hash here should be the parent block hash, or the
        /// signal will not be recognized.
        /// event InitiateChange(bytes32 indexed _parent_hash, address[] _new_set);
        /// </summary>
        public bool CheckInitiateChangeEvent(BlockHeader blockHeader, TxReceipt[] receipts, out Address[] addresses)
        {
            var logEntry = GetSearchLogEntry(InitiateChange, blockHeader.ParentHash);
            if (blockHeader.TryFindLog(receipts, logEntry, out var foundEntry, 
                logsFindOrder:FindOrder.Ascending /* tracing forwards, parity inconsistency issue */))
            {
                addresses = DecodeAddresses(foundEntry.Data);
                return true;                
            }

            addresses = null;
            return false;
        }

        private Address[] DecodeAddresses(byte[] data)
        {
            var objects = DecodeReturnData(nameof(GetValidators), data);
            return (Address[]) objects[0];
        }
        
        public void EnsureSystemAccount()
        {
            EnsureSystemAccount(_stateProvider);
        }
    }
}
