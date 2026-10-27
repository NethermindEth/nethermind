// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
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
            Address contractAddress,
            IWorldState stateProvider,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            ISigner signer)
            : base(transactionProcessor, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)))
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
        public void FinalizeChange(BlockHeader blockHeader) => TryCall(blockHeader, FinalizeChangeFunction, Address.SystemUser, UnlimitedGas, default, out _);

        private static readonly ContractFunction<ValueTuple, ValueTuple> FinalizeChangeFunction = new AbiSignature("finalizeChange").With().Returns();

        private static readonly ContractFunction<ValueTuple, Address[]> GetValidatorsFunction = new AbiSignature("getValidators").With().Returns(AbiType.Array(AbiType.Address));

        /// <summary>
        /// Get current validator set (last enacted or initial if no changes ever made)
        /// function getValidators() constant returns (address[] _validators);
        /// </summary>
        public Address[] GetValidators(BlockHeader parentHeader) => Constant.Call(parentHeader, GetValidatorsFunction, Address.Zero, default);

        internal static readonly AbiSignature<(ValueHash256, Address[])> InitiateChangeEvent = new AbiSignature("InitiateChange").With(AbiType.Bytes32, AbiType.Array(AbiType.Address));

        /// <summary>Non-indexed parameters of <see cref="InitiateChangeEvent"/>, encoded in the log data.</summary>
        internal static readonly AbiSignature<Address[]> InitiateChangeData = new AbiSignature().With(AbiType.Array(AbiType.Address));

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
            LogEntry logEntry = GetSearchLogEntry(InitiateChangeEvent.Hash, blockHeader.ParentHash);
            if (blockHeader.TryFindLog(receipts, logEntry, out LogEntry foundEntry,
                logsFindOrder: FindOrder.Ascending /* tracing forwards, parity inconsistency issue */))
            {
                addresses = DecodeData(InitiateChangeData, foundEntry.Data);
                return true;
            }

            addresses = null;
            return false;
        }

        public void EnsureSystemAccount() => EnsureSystemAccount(_stateProvider);
    }
}
