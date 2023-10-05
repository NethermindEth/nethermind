// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Facade;

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
        private readonly IWorldState _stateProvider;
        private readonly ISigner _signer;

        private IConstantContract Constant { get; }

        public ValidatorContract(
            ITransactionProcessor transactionProcessor,
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IWorldState stateProvider,
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
        public void FinalizeChange(BlockHeader blockHeader) => TryCall(blockHeader, nameof(FinalizeChange), Address.SystemUser, UnlimitedGas, null, out _);

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
                logsFindOrder: FindOrder.Ascending /* tracing forwards, parity inconsistency issue */))
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
            return (Address[])objects[0];
        }

        public void EnsureSystemAccount()
        {
            EnsureSystemAccount(_stateProvider);
        }
    }
}
