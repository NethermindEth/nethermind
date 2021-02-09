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
        public bool EmitInitiateChangeCallable(BlockHeader parentHeader) => Constant.Call<bool>(parentHeader, nameof(EmitInitiateChangeCallable), Address.SystemUser);
        
        /// <summary>
        /// Emits the `InitiateChange` event to pass a new validator set to the validator nodes.
        /// Called automatically by one of the current validator's nodes when the `emitInitiateChangeCallable` getter
        /// returns `true` (when some validator needs to be removed as malicious or the validator set needs to be
        /// updated at the beginning of a new staking epoch). The new validator set is passed to the validator nodes
        /// through the `InitiateChange` event and saved for later use by the `finalizeChange` function.
        /// See https://openethereum.github.io/wiki/Validator-Set.html for more info about the `InitiateChange` event.
        /// </summary>
        public Transaction EmitInitiateChange() => GenerateTransaction<GeneratedTransaction>(nameof(EmitInitiateChange), _signer.Address);
        
        // This was mistakenly put here in POSDAO it should belong to ReportingValidatorContract
        public bool ShouldValidatorReport(BlockHeader parentHeader, Address validatorAddress, Address maliciousMinerAddress, in UInt256 blockNumber) => 
            Constant.Call<bool>(parentHeader, nameof(ShouldValidatorReport), Address.SystemUser, validatorAddress, maliciousMinerAddress, blockNumber);
    }
}
