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

using System;
using Microsoft.Extensions.Logging;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.Core2.Containers;
using Attestation = Nethermind.BeaconNode.Containers.Attestation;
using AttesterSlashing = Nethermind.BeaconNode.Containers.AttesterSlashing;
using BeaconBlock = Nethermind.BeaconNode.Containers.BeaconBlock;
using BeaconBlockBody = Nethermind.BeaconNode.Containers.BeaconBlockBody;
using BeaconState = Nethermind.BeaconNode.Containers.BeaconState;
using Deposit = Nethermind.BeaconNode.Containers.Deposit;
using ProposerSlashing = Nethermind.BeaconNode.Containers.ProposerSlashing;

namespace Nethermind.BeaconNode
{
    internal static class LogDebug
    { 
        // 6bxx debug

        // 60xx debug - worker
        public static readonly Action<ILogger, Exception?> BeaconNodeWorkerStarting =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6000, nameof(BeaconNodeWorkerStarting)),
                "Beacon node worker starting.");
        public static readonly Action<ILogger, Exception?> BeaconNodeWorkerStarted =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6001, nameof(BeaconNodeWorkerStarted)),
                "Beacon node worker started.");
        public static readonly Action<ILogger, Exception?> BeaconNodeWorkerStopping =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6002, nameof(BeaconNodeWorkerStopping)),
                "Beacon node worker stopping.");
        public static readonly Action<ILogger, int, Exception?> BeaconNodeWorkerExecuteExiting =
            LoggerMessage.Define<int>(LogLevel.Debug,
                new EventId(6003, nameof(BeaconNodeWorkerExecuteExiting)),
                "Beacon node worker execute thread exiting [{ThreadId}].");
        
        // 61xx debug - state transition
        public static readonly Action<ILogger, Deposit, BeaconState, Exception?> ProcessDeposit =
            LoggerMessage.Define<Deposit, BeaconState>(LogLevel.Debug,
                new EventId(6100, nameof(ProcessDeposit)),
                "Process block operation deposit {Deposit} for state {BeaconState}.");

        public static readonly Action<ILogger, Slot, BeaconState, Exception?> ProcessSlots =
            LoggerMessage.Define<Slot, BeaconState>(LogLevel.Debug,
                new EventId(6101, nameof(ProcessSlots)),
                "Process slots to {Slot} for state {BeaconState}");
        
        public static readonly Action<ILogger, Slot, BeaconState, Exception?> ProcessSlot =
            LoggerMessage.Define<Slot, BeaconState>(LogLevel.Debug,
                new EventId(6102, nameof(ProcessSlot)),
                "Process current slot {Slot} for state {BeaconState}");
        
        public static readonly Action<ILogger, BeaconState, Exception?> ProcessJustificationAndFinalization =
            LoggerMessage.Define<BeaconState>(LogLevel.Debug,
                new EventId(6103, nameof(ProcessJustificationAndFinalization)),
                "Process epoch justification and finalization state {BeaconState}");

        public static readonly Action<ILogger,  BeaconState, Exception?> ProcessEpoch =
            LoggerMessage.Define<BeaconState>(LogLevel.Debug,
                new EventId(6104, nameof(ProcessEpoch)),
                "Process end of epoch for state {BeaconState}");

        public static readonly Action<ILogger, bool, BeaconBlock, BeaconState, Exception?> ProcessBlock =
            LoggerMessage.Define<bool, BeaconBlock, BeaconState>(LogLevel.Debug,
                new EventId(6105, nameof(ProcessBlock)),
                "Process (validate {Validate}) block {BeaconBlock} for state {BeaconState}");
        
        public static readonly Action<ILogger, BeaconBlock,  Exception?> ProcessBlockHeader =
            LoggerMessage.Define<BeaconBlock>(LogLevel.Debug,
                new EventId(6106, nameof(ProcessBlockHeader)),
                "Process block header for block {BeaconBlock}");

        public static readonly Action<ILogger,  BeaconBlockBody, Exception?> ProcessRandao =
            LoggerMessage.Define<BeaconBlockBody>(LogLevel.Debug,
                new EventId(6107, nameof(ProcessRandao)),
                "Process block randao for block body {BeaconBlockBody}");

        public static readonly Action<ILogger,  BeaconBlockBody, Exception?> ProcessEth1Data =
            LoggerMessage.Define<BeaconBlockBody>(LogLevel.Debug,
                new EventId(6108, nameof(ProcessEth1Data)),
                "Process block ETH1 data for block body {BeaconBlockBody}");

        public static readonly Action<ILogger,  BeaconBlockBody, Exception?> ProcessOperations =
            LoggerMessage.Define<BeaconBlockBody>(LogLevel.Debug,
                new EventId(6109, nameof(ProcessOperations)),
                "Process block operations for block body {BeaconBlockBody}");
        
        public static readonly Action<ILogger, ProposerSlashing, Exception?> ProcessProposerSlashing =
            LoggerMessage.Define<ProposerSlashing>(LogLevel.Debug,
                new EventId(6110, nameof(ProcessProposerSlashing)),
                "Process block operation proposer slashing {ProposerSlashing}");
        
        public static readonly Action<ILogger, AttesterSlashing, Exception?> ProcessAttesterSlashing =
            LoggerMessage.Define<AttesterSlashing>(LogLevel.Debug,
                new EventId(6111, nameof(ProcessAttesterSlashing)),
                "Process block operation attester slashing {AttesterSlashing}");
        
        public static readonly Action<ILogger, Attestation, BeaconState, Exception?> ProcessAttestation =
            LoggerMessage.Define<Attestation, BeaconState>(LogLevel.Debug,
                new EventId(6112, nameof(ProcessAttestation)),
                "Process block operation attestation {Attestation} for state {BeaconState}.");
        
        public static readonly Action<ILogger, VoluntaryExit, BeaconState, Exception?> ProcessVoluntaryExit =
            LoggerMessage.Define<VoluntaryExit, BeaconState>(LogLevel.Debug,
                new EventId(6113, nameof(ProcessVoluntaryExit)),
                "Process block operation voluntary exit {VoluntaryExit} for state {BeaconState}.");
        
        public static readonly Action<ILogger,  BeaconState, Exception?> ProcessRewardsAndPenalties =
            LoggerMessage.Define<BeaconState>(LogLevel.Debug,
                new EventId(6114, nameof(ProcessRewardsAndPenalties)),
                "Process epoch rewards and penalties state {BeaconState}");

        public static readonly Action<ILogger,  BeaconState, Exception?> ProcessFinalUpdates =
            LoggerMessage.Define<BeaconState>(LogLevel.Debug,
                new EventId(6115, nameof(ProcessFinalUpdates)),
                "Process epoch final updates state {BeaconState}");

        public static readonly Action<ILogger,  BeaconState, Exception?> ProcessRegistryUpdates =
            LoggerMessage.Define<BeaconState>(LogLevel.Debug,
                new EventId(6116, nameof(ProcessRegistryUpdates)),
                "Process epoch registry updates state {BeaconState}");

        public static readonly Action<ILogger,  BeaconState, Exception?> ProcessSlashings =
            LoggerMessage.Define<BeaconState>(LogLevel.Debug,
                new EventId(6117, nameof(ProcessSlashings)),
                "Process epoch slashings state {BeaconState}");

        public static readonly Action<ILogger, bool, BeaconState, BeaconBlock, Exception?> StateTransition =
            LoggerMessage.Define<bool, BeaconState, BeaconBlock>(LogLevel.Debug,
                new EventId(6118, nameof(StateTransition)),
                "State transition (validate {Validate}) for state {BeaconState} with block {BeaconBlock}");

        public static readonly Action<ILogger, ValidatorIndex, string, Gwei, Exception?> RewardForValidator =
            LoggerMessage.Define<ValidatorIndex, string, Gwei>(LogLevel.Debug,
                new EventId(6119, nameof(RewardForValidator)),
                "Reward for validator {ValidatorIndex}: {RewardName} +{Reward}.");

        public static readonly Action<ILogger, ValidatorIndex, string, Gwei, Exception?> PenaltyForValidator =
            LoggerMessage.Define<ValidatorIndex, string, Gwei>(LogLevel.Debug,
                new EventId(6120, nameof(PenaltyForValidator)),
                "Penalty for validator {ValidatorIndex}: {PenaltyName} -{Penalty}.");

        // 62xx debug - fork choice

        public static readonly Action<ILogger, BeaconBlock, BeaconState, Hash32, Exception?> AddedBlockToStore =
            LoggerMessage.Define<BeaconBlock, BeaconState, Hash32>(LogLevel.Debug,
                new EventId(6200, nameof(AddedBlockToStore)),
                "Store added block {BeaconBlock} generating state {BeaconState}, with signing root {SigningRoot}");
        public static readonly Action<ILogger, BeaconNode.Containers.Checkpoint, Exception?> UpdateJustifiedCheckpoint =
            LoggerMessage.Define<BeaconNode.Containers.Checkpoint>(LogLevel.Debug,
                new EventId(6201, nameof(UpdateJustifiedCheckpoint)),
                "Updated justified checkpoint {JustifiedCheckpoint}");
        public static readonly Action<ILogger, BeaconNode.Containers.Checkpoint, Exception?> UpdateBestJustifiedCheckpoint =
            LoggerMessage.Define<BeaconNode.Containers.Checkpoint>(LogLevel.Debug,
                new EventId(6202, nameof(UpdateBestJustifiedCheckpoint)),
                "Updated best justified checkpoint {JustifiedCheckpoint}");
        public static readonly Action<ILogger, BeaconNode.Containers.Checkpoint, Exception?> UpdateFinalizedCheckpoint =
            LoggerMessage.Define<BeaconNode.Containers.Checkpoint>(LogLevel.Debug,
                new EventId(6203, nameof(UpdateFinalizedCheckpoint)),
                "Updated finalized checkpoint {FinalizedCheckpoint}");

        // 63xx - chain start

        public static readonly Action<ILogger, Hash32, ulong, int, Exception?> TryGenesis =
            LoggerMessage.Define<Hash32, ulong, int>(LogLevel.Debug,
                new EventId(6300, nameof(TryGenesis)),
                "Try genesis with ETH1 block {Eth1BlockHash}, time {Eth1Timestamp}, with {DepositCount} deposits.");

        // 64xx debug - block producer

        public static readonly Action<ILogger, Slot, BlsSignature, Slot, Exception?> NewBlockSkippedSlots =
            LoggerMessage.Define<Slot, BlsSignature, Slot>(LogLevel.Debug,
                new EventId(6400, nameof(NewBlockSkippedSlots)),
                "Request for new block for slot {Slot} for randao {RandaoReveal} is skipping from parent slot {ParentSlot}.");
        
        // 7bxx - mock

        public static readonly Action<ILogger, ulong, Exception?> QuickStartStoreCreated =
            LoggerMessage.Define<ulong>(LogLevel.Debug,
                new EventId(7100, nameof(QuickStartStoreCreated)),
                "Quick start genesis store created with genesis time {GenesisTime:n0}.");
        public static readonly Action<ILogger, ValidatorIndex, string, Exception?> QuickStartAddValidator =
            LoggerMessage.Define<ValidatorIndex, string>(LogLevel.Debug,
                new EventId(7300, nameof(QuickStartAddValidator)),
                "Quick start adding deposit for mocked validator {ValidatorIndex} with public key {PublicKey}.");

    }
}