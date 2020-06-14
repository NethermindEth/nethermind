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
using Nethermind.Core2.P2p;

namespace Nethermind.BeaconNode
{
    internal static class LogDebug
    { 
        // Event IDs: ABxx (based on Theory of Reply Codes)
        
        // Event ID Type:
        // 6bxx debug - general
        // 7bxx debug - test
        // 1bxx info - preliminary
        // 2bxx info - completion
        // 3bxx info - intermediate
        // 8bxx info - finalization
        // 4bxx warning
        // 5bxx error
        // 9bxx critical
        
        // Event ID Category:
        // a0xx core service, worker, configuration, peering
        // a1xx beacon chain, incl. state transition
        // a2xx fork choice
        // a3xx deposit contract, Eth1, genesis
        // a4xx honest validator, API
        // a5xx custody game
        // a6xx shard data chains
        // a9xx miscellaneous / other
        
        
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
        public static readonly Action<ILogger, RpcDirection, PeeringStatus, string, Exception?> SendingStatusToPeer =
            LoggerMessage.Define<RpcDirection, PeeringStatus, string>(LogLevel.Debug,
                new EventId(6004, nameof(SendingStatusToPeer)),
                "Sending status {RpcDirection} {Status} to peer {PeerId}.");
        public static readonly Action<ILogger, string, Epoch, Root, Slot, Exception?> PeerBehind =
            LoggerMessage.Define<string, Epoch, Root, Slot>(LogLevel.Debug,
                new EventId(6004, nameof(PeerBehind)),
                "Peer {PeerId} is behind, no need to sync (peer finalized epoch {FinalizedSlot}, head {PeerHeadRoot}, slot {PeerHeadSlot}).");
        
        // 61xx debug - state transition
        public static readonly Action<ILogger, Deposit, BeaconState, Exception?> ProcessDeposit =
            LoggerMessage.Define<Deposit, BeaconState>(LogLevel.Debug,
                new EventId(6100, nameof(ProcessDeposit)),
                "Process operation deposit {Deposit} for state {BeaconState}.");

        public static readonly Action<ILogger, BeaconState, Slot, Exception?> ProcessSlots =
            LoggerMessage.Define<BeaconState, Slot>(LogLevel.Debug,
                new EventId(6101, nameof(ProcessSlots)),
                "Process slots for state {BeaconState} to {Slot}");
        
        public static readonly Action<ILogger, Slot, BeaconState, Exception?> ProcessSlot =
            LoggerMessage.Define<Slot, BeaconState>(LogLevel.Debug,
                new EventId(6102, nameof(ProcessSlot)),
                "Process slot {Slot} for state {BeaconState}");
        
        public static readonly Action<ILogger, ulong, Exception?> ProcessJustificationAndFinalization =
            LoggerMessage.Define<ulong>(LogLevel.Debug,
                new EventId(6103, nameof(ProcessJustificationAndFinalization)),
                "Process slot {Slot} epoch justification and finalization");

        public static readonly Action<ILogger, ulong, Exception?> ProcessEpoch =
            LoggerMessage.Define<ulong>(LogLevel.Debug,
                new EventId(6104, nameof(ProcessEpoch)),
                "Process slot {Slot} end of epoch");

        public static readonly Action<ILogger, BeaconBlock, BeaconState, Exception?> ProcessBlock =
            LoggerMessage.Define<BeaconBlock, BeaconState>(LogLevel.Debug,
                new EventId(6105, nameof(ProcessBlock)),
                "Process block {BeaconBlock} for state {BeaconState}");
        
        public static readonly Action<ILogger, ulong, BeaconBlockHeader,  Exception?> ProcessingBlockHeader =
            LoggerMessage.Define<ulong, BeaconBlockHeader>(LogLevel.Debug,
                new EventId(6106, nameof(ProcessingBlockHeader)),
                "Processing block header for slot {Slot} setting header {BeaconBlockHeader}");

        public static readonly Action<ILogger, Slot, BlsSignature, Exception?> ProcessRandao =
            LoggerMessage.Define<Slot, BlsSignature>(LogLevel.Debug,
                new EventId(6107, nameof(ProcessRandao)),
                "Process block randao for slot {Slot}, randao reveal {RandaoReveal}");

        public static readonly Action<ILogger, ulong, Eth1Data, Exception?> ProcessEth1Data =
            LoggerMessage.Define<ulong, Eth1Data>(LogLevel.Debug,
                new EventId(6108, nameof(ProcessEth1Data)),
                "Process block ETH1 data for slot {Slot}, data {Eth1Data}");

        public static readonly Action<ILogger, ulong, BeaconBlockBody, Exception?> ProcessOperations =
            LoggerMessage.Define<ulong, BeaconBlockBody>(LogLevel.Debug,
                new EventId(6109, nameof(ProcessOperations)),
                "Process block operations for slot {Slot} from block body {BeaconBlockBody}");
        
        public static readonly Action<ILogger, ProposerSlashing, Exception?> ProcessProposerSlashing =
            LoggerMessage.Define<ProposerSlashing>(LogLevel.Debug,
                new EventId(6110, nameof(ProcessProposerSlashing)),
                "Process operation proposer slashing {ProposerSlashing}");
        
        public static readonly Action<ILogger, AttesterSlashing, Exception?> ProcessAttesterSlashing =
            LoggerMessage.Define<AttesterSlashing>(LogLevel.Debug,
                new EventId(6111, nameof(ProcessAttesterSlashing)),
                "Process operation attester slashing {AttesterSlashing}");
        
        public static readonly Action<ILogger, Attestation, Exception?> ProcessAttestation =
            LoggerMessage.Define<Attestation>(LogLevel.Debug,
                new EventId(6112, nameof(ProcessAttestation)),
                "Process operation attestation {Attestation}.");
        
        public static readonly Action<ILogger, VoluntaryExit, Exception?> ProcessVoluntaryExit =
            LoggerMessage.Define<VoluntaryExit>(LogLevel.Debug,
                new EventId(6113, nameof(ProcessVoluntaryExit)),
                "Process operation voluntary exit {VoluntaryExit}.");
        
        public static readonly Action<ILogger, ulong, Exception?> ProcessRewardsAndPenalties =
            LoggerMessage.Define<ulong>(LogLevel.Debug,
                new EventId(6114, nameof(ProcessRewardsAndPenalties)),
                "Process epoch rewards and penalties state slot {Slot}");

        public static readonly Action<ILogger, ulong, Exception?> ProcessFinalUpdates =
            LoggerMessage.Define<ulong>(LogLevel.Debug,
                new EventId(6115, nameof(ProcessFinalUpdates)),
                "Process epoch final updates slot {Slot}");

        public static readonly Action<ILogger, ulong, Exception?> ProcessRegistryUpdates =
            LoggerMessage.Define<ulong>(LogLevel.Debug,
                new EventId(6116, nameof(ProcessRegistryUpdates)),
                "Process epoch registry updates slot {Slot}");

        public static readonly Action<ILogger, ulong, Exception?> ProcessSlashings =
            LoggerMessage.Define<ulong>(LogLevel.Debug,
                new EventId(6117, nameof(ProcessSlashings)),
                "Process epoch slashings slot {Slot}");

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

        
        public static readonly Action<ILogger, Root, BeaconBlock, Root, ValidatorIndex, bool, Exception?> VerifiedBlockSignature =
            LoggerMessage.Define<Root, BeaconBlock, Root, ValidatorIndex, bool>(LogLevel.Debug,
                new EventId(6121, nameof(VerifiedBlockSignature)),
                "Verified signature block {BlockRoot} ({BeaconBlock}), signing root {SigningRoot} by proposer {ValidatorIndex}: {IsValid}.");

        // 62xx debug - fork choice

        public static readonly Action<ILogger, BeaconBlock, BeaconState, Root, Exception?> AddedBlockToStore =
            LoggerMessage.Define<BeaconBlock, BeaconState, Root>(LogLevel.Debug,
                new EventId(6200, nameof(AddedBlockToStore)),
                "Store added block {BlockRoot} ({BeaconBlock}) generating state {BeaconState}.");
        public static readonly Action<ILogger, Checkpoint, Exception?> UpdateJustifiedCheckpoint =
            LoggerMessage.Define<Checkpoint>(LogLevel.Debug,
                new EventId(6201, nameof(UpdateJustifiedCheckpoint)),
                "Updated justified checkpoint {JustifiedCheckpoint}");
        public static readonly Action<ILogger, Checkpoint, Exception?> UpdateBestJustifiedCheckpoint =
            LoggerMessage.Define<Checkpoint>(LogLevel.Debug,
                new EventId(6202, nameof(UpdateBestJustifiedCheckpoint)),
                "Updated best justified checkpoint {JustifiedCheckpoint}");
        public static readonly Action<ILogger, Checkpoint, Exception?> UpdateFinalizedCheckpoint =
            LoggerMessage.Define<Checkpoint>(LogLevel.Debug,
                new EventId(6203, nameof(UpdateFinalizedCheckpoint)),
                "Updated finalized checkpoint {FinalizedCheckpoint}");

        // 63xx - chain start

        public static readonly Action<ILogger, Bytes32, ulong, uint, Exception?> TryGenesis =
            LoggerMessage.Define<Bytes32, ulong, uint>(LogLevel.Debug,
                new EventId(6300, nameof(TryGenesis)),
                "Try genesis with ETH1 block {Eth1BlockHash}, time {Eth1Timestamp}, with {DepositCount} deposits.");

        // 64xx debug - block producer

        public static readonly Action<ILogger, Slot, BlsSignature, Slot, Exception?> NewBlockSkippedSlots =
            LoggerMessage.Define<Slot, BlsSignature, Slot>(LogLevel.Debug,
                new EventId(6400, nameof(NewBlockSkippedSlots)),
                "Request for new block for slot {Slot} for randao {RandaoReveal} is skipping from parent slot {ParentSlot}.");
        public static readonly Action<ILogger, ulong, string, BeaconBlock, string, Exception?> NewBlockProduced
            = LoggerMessage.Define<ulong, string, BeaconBlock, string>(LogLevel.Debug,
                new EventId(6401, nameof(NewBlockProduced)),
                "New block produced for slot {Slot} with RANDAO reveal {RandaoReveal}, block {BeaconBlock}, and graffiti {Graffiti}");
        public static readonly Action<ILogger, BeaconBlock, Exception?> PublishingBlockToNetwork
            = LoggerMessage.Define<BeaconBlock>(LogLevel.Debug,
                new EventId(6402, nameof(PublishingBlockToNetwork)),
                "Publishing block {BeaconBlock} to network");
        public static readonly Action<ILogger, int, Epoch, Root, Exception?> GettingMissingValidatorDutiesForCache =
            LoggerMessage.Define<int, Epoch, Root>(LogLevel.Debug,
                new EventId(6403, nameof(GettingMissingValidatorDutiesForCache)),
                "Validator duties for {0} validators are missing from the cache and need to be calculated for epoch {Epoch} with starting root {EpochStartRoot}.");
        public static readonly Action<ILogger, Attestation, Exception?> PublishingAttestationToNetwork
            = LoggerMessage.Define<Attestation>(LogLevel.Debug,
                new EventId(6404, nameof(PublishingAttestationToNetwork)),
                "Publishing attestation {Attestation} to network");
    }
}