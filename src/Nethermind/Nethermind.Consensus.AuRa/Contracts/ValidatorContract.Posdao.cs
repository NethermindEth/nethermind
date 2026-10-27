// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public partial interface IValidatorContract
    {
        /// <summary>
        /// Returns a boolean flag indicating whether the `emitInitiateChange` function can be called at the moment. Used by a validator's node and `TxPermission` contract (to deny dummy calling).
        /// </summary>
        bool EmitInitiateChangeCallable(BlockHeader parentHeader);

        /// <summary>
        /// Emits the `InitiateChange` event to pass a new validator set to the validator nodes.
        /// Called automatically by one of the current validator's nodes when the `emitInitiateChangeCallable` getter
        /// returns `true` (when some validator needs to be removed as malicious or the validator set needs to be
        /// updated at the beginning of a new staking epoch). The new validator set is passed to the validator nodes
        /// through the `InitiateChange` event and saved for later use by the `finalizeChange` function.
        /// See https://openethereum.github.io/wiki/Validator-Set.html for more info about the `InitiateChange` event.
        /// </summary>
        Transaction EmitInitiateChange();

        bool ShouldValidatorReport(BlockHeader parentHeader, Address validatorAddress, Address maliciousMinerAddress, in UInt256 blockNumber);
    }

    public partial class ValidatorContract
    {
        /// <summary>
        /// Returns a boolean flag indicating whether the `emitInitiateChange` function can be called at the moment. Used by a validator's node and `TxPermission` contract (to deny dummy calling).
        /// </summary>
        public bool EmitInitiateChangeCallable(BlockHeader parentHeader) => Constant.Call(parentHeader, EmitInitiateChangeCallableFunction, Address.SystemUser, default);

        private static readonly ContractFunction<ValueTuple, bool> EmitInitiateChangeCallableFunction = new AbiSignature("emitInitiateChangeCallable").With().Returns(AbiType.Bool);

        /// <summary>
        /// Emits the `InitiateChange` event to pass a new validator set to the validator nodes.
        /// Called automatically by one of the current validator's nodes when the `emitInitiateChangeCallable` getter
        /// returns `true` (when some validator needs to be removed as malicious or the validator set needs to be
        /// updated at the beginning of a new staking epoch). The new validator set is passed to the validator nodes
        /// through the `InitiateChange` event and saved for later use by the `finalizeChange` function.
        /// See https://openethereum.github.io/wiki/Validator-Set.html for more info about the `InitiateChange` event.
        /// </summary>
        public Transaction EmitInitiateChange() => GenerateTransaction<GeneratedTransaction>(AbiCodec.Encode(EmitInitiateChangeSignature, default), _signer.Address);

        private static readonly AbiSignature<ValueTuple> EmitInitiateChangeSignature = new AbiSignature("emitInitiateChange").With();

        // This was mistakenly put here in POSDAO it should belong to ReportingValidatorContract
        public bool ShouldValidatorReport(BlockHeader parentHeader, Address validatorAddress, Address maliciousMinerAddress, in UInt256 blockNumber) =>
            Constant.Call(parentHeader, ShouldValidatorReportFunction, Address.SystemUser, (validatorAddress, maliciousMinerAddress, blockNumber));

        private static readonly ContractFunction<(Address, Address, UInt256), bool> ShouldValidatorReportFunction =
            new AbiSignature("shouldValidatorReport").With(AbiType.Address, AbiType.Address, AbiType.UInt256).Returns(AbiType.Bool);
    }
}
