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
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.BeaconNode
{
    internal static class Log
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
        
        
        // 1bxx preliminary

        public static readonly Action<ILogger, string, string, string, int, Exception?> BeaconNodeWorkerExecuteStarted =
            LoggerMessage.Define<string, string, string, int>(LogLevel.Information,
                new EventId(1000, nameof(BeaconNodeWorkerExecuteStarted)),
                "Beacon Node {ProductTokenVersion} worker started; data directory '{DataDirectory}' (environment {Environment}) [{ThreadId}]");
        
        public static readonly Action<ILogger, Bytes32, ulong, uint, Exception?> InitializeBeaconState =
            LoggerMessage.Define<Bytes32, ulong, uint>(LogLevel.Information,
                new EventId(1300, nameof(InitializeBeaconState)),
                "Initialise beacon state from ETH1 block {Eth1BlockHash}, time {Eth1Timestamp}, with {DepositCount} deposits.");

        // 2bxx completion
        
        public static readonly Action<ILogger, ulong, ulong, long, int, Exception?> WorkerStoreAvailableTickStarted =
            LoggerMessage.Define<ulong, ulong, long, int>(LogLevel.Information,
                new EventId(2000, nameof(WorkerStoreAvailableTickStarted)),
                "Store available with genesis time {GenesisTime}, at clock time {Time} (slot {SlotValue}), starting clock tick [{ThreadId}].");

        public static readonly Action<ILogger, string, Slot, Root, Slot, Exception?> RequestingBlocksFromAheadPeer =
            LoggerMessage.Define<string, Slot, Root, Slot>(LogLevel.Information,
                new EventId(2001, nameof(RequestingBlocksFromAheadPeer)),
                "Peer {PeerId} is ahead, requesting blocks from finalized slot {FinalizedSlot} up to head {PeerHeadRoot}, slot {PeerHeadSlot}.");
        
        public static readonly Action<ILogger, Root, BeaconState, Root, BeaconBlock, Exception?> ValidatedStateTransition =
            LoggerMessage.Define<Root, BeaconState, Root, BeaconBlock>(LogLevel.Information,
                new EventId(2100, nameof(ValidatedStateTransition)),
                "Validated state transition to new state root {StateRoot} ({BeaconState}) by block {BlockRoot} ({BeaconBlock})");

        public static readonly Action<ILogger, Fork, Root, ulong, BeaconState, BeaconBlock, Checkpoint, Exception?> CreateGenesisStore =
            LoggerMessage.Define<Fork, Root, ulong, BeaconState, BeaconBlock, Checkpoint>(LogLevel.Information,
                new EventId(2200, nameof(CreateGenesisStore)),
                "Initializing store on fork {Fork} with anchor root {AnchorRoot}, genesis {GenesisTime} (state {AnchorState}, block {AnchorBlock}, checkpoint {AnchorCheckpoint})");

        public static readonly Action<ILogger, Attestation, Exception?> OnAttestation =
            LoggerMessage.Define<Attestation>(LogLevel.Information,
                new EventId(2201, nameof(OnAttestation)),
                "Fork choice received attestation {Attestation}");

        public static readonly Action<ILogger, Root, BeaconBlock, BlsSignature, Exception?> OnBlock =
            LoggerMessage.Define<Root, BeaconBlock, BlsSignature>(LogLevel.Information,
                new EventId(2202, nameof(OnBlock)),
                "Fork choice received block {BlockRoot} ({BeaconBlock}) with signature {Signature}");

        public static readonly Action<ILogger, Epoch, Slot, ulong, Exception?> OnTickNewEpoch =
            LoggerMessage.Define<Epoch, Slot, ulong>(LogLevel.Information,
                new EventId(2203, nameof(OnTickNewEpoch)),
                "Fork choice new epoch {Epoch} at slot {Slot} time {Time:n0}");

        public static readonly Action<ILogger, long, Exception?> GenesisCountdown =
            LoggerMessage.Define<long>(LogLevel.Information,
                new EventId(2204, nameof(GenesisCountdown)),
                "Countdown {Time:n0} seconds to expected genesis.");

        // 4bxx warning

        public static readonly Action<ILogger, string, ForkVersion, Slot, Epoch, ForkVersion, Exception?> PeerStatusInvalidForkVersion =
            LoggerMessage.Define<string, ForkVersion, Slot, Epoch, ForkVersion>(LogLevel.Warning,
                new EventId(4000, nameof(PeerStatusInvalidForkVersion)),
                "Disconnecting peer {PeerId} because it has fork version {PeerForkVersion} at slot {PeerSlot} (epoch {PeerEpoch}) different from expected {ExpectedForkVersion}.");
        public static readonly Action<ILogger, string, Root, Epoch, Root, Exception?> PeerStatusInvalidFinalizedCheckpoint =
            LoggerMessage.Define<string, Root, Epoch, Root>(LogLevel.Warning,
                new EventId(4001, nameof(PeerStatusInvalidFinalizedCheckpoint)),
                "Disconnecting peer {PeerId} because it has finalized checkpoint {PeerFinalizedRoot} at epoch {PeerFinalizedEpoch} different from expected {ExpectedRoot}.");

        public static readonly Action<ILogger, CommitteeIndex, Slot, int, Exception?> InvalidIndexedAttestationBit1 =
            LoggerMessage.Define<CommitteeIndex, Slot, int>(LogLevel.Warning,
                new EventId(4100, nameof(InvalidIndexedAttestationBit1)),
                "Invalid indexed attestation from committee {CommitteeIndex} for slot {Slot}, because it has {BitIndicesCount} bit 1 indices.");
        public static readonly Action<ILogger, CommitteeIndex, Slot, int, ulong, Exception?> InvalidIndexedAttestationTooMany =
            LoggerMessage.Define<CommitteeIndex, Slot, int, ulong>(LogLevel.Warning,
                new EventId(4101, nameof(InvalidIndexedAttestationTooMany)),
                "Invalid indexed attestation from committee {CommitteeIndex} for slot {Slot}, because it has total indices {TotalIndices}, more than the maximum validators per committee {MaximumValidatorsPerCommittee}.");
        public static readonly Action<ILogger, CommitteeIndex, Slot, int, Exception?> InvalidIndexedAttestationIntersection =
            LoggerMessage.Define<CommitteeIndex, Slot, int>(LogLevel.Warning,
                new EventId(4102, nameof(InvalidIndexedAttestationIntersection)),
                "Invalid indexed attestation from committee {CommitteeIndex} for slot {Slot}, because it has {IntersectingValidatorCount} validator indexes in common between custody bit 0 and custody bit 1.");
        public static readonly Action<ILogger, CommitteeIndex, Slot, int, int, Exception?> InvalidIndexedAttestationNotSorted =
            LoggerMessage.Define<CommitteeIndex, Slot, int, int>(LogLevel.Warning,
                new EventId(4103, nameof(InvalidIndexedAttestationNotSorted)),
                "Invalid indexed attestation from committee {CommitteeIndex} for slot {Slot}, because custody bit {CustodyBit} index {IndexNumber} is not sorted.");
        public static readonly Action<ILogger, CommitteeIndex, Slot, Exception?> InvalidIndexedAttestationSignature =
                LoggerMessage.Define<CommitteeIndex, Slot>(LogLevel.Warning,
                    new EventId(4104, nameof(InvalidIndexedAttestationSignature)),
                "Invalid indexed attestation from committee {CommitteeIndex} for slot {Slot}, because the aggregate signature does not match.");
        public static readonly Action<ILogger, CommitteeIndex, Slot, int, int, Exception?> InvalidIndexedAttestationNotUnique =
            LoggerMessage.Define<CommitteeIndex, Slot, int, int>(LogLevel.Warning,
                new EventId(4105, nameof(InvalidIndexedAttestationNotUnique)),
                "Invalid indexed attestation from committee {CommitteeIndex} for slot {Slot}, because custody bit {CustodyBit} index {IndexNumber} is not unique.");

        public static readonly Action<ILogger, Exception?> ApiErrorGetVersion =
            LoggerMessage.Define(LogLevel.Warning,
                new EventId(4400, nameof(ApiErrorGetVersion)),
                "Exception result from API get version.");
        public static readonly Action<ILogger, Exception?> ApiErrorGetGenesisTime =
            LoggerMessage.Define(LogLevel.Warning,
                new EventId(4401, nameof(ApiErrorGetGenesisTime)),
                "Exception result from API get genesis time.");
        public static readonly Action<ILogger, Exception?> ApiErrorGetFork =
            LoggerMessage.Define(LogLevel.Warning,
                new EventId(4402, nameof(ApiErrorGetFork)),
                "Exception result from API get fork.");
        public static readonly Action<ILogger, Exception?> ApiErrorValidatorDuties =
            LoggerMessage.Define(LogLevel.Warning,
                new EventId(4403, nameof(ApiErrorValidatorDuties)),
                "Exception result from API validator duties.");
        public static readonly Action<ILogger, Exception?> ApiErrorNewBlock =
            LoggerMessage.Define(LogLevel.Warning,
                new EventId(4404, nameof(ApiErrorNewBlock)),
                "Exception result from API Block (get).");
        public static readonly Action<ILogger, Slot, Slot, Root, Slot, Exception?> NoBlocksSinceEth1VotingPeriodDefaulting =
            LoggerMessage.Define<Slot, Slot, Root, Slot>(LogLevel.Warning,
                new EventId(4405, nameof(NoBlocksSinceEth1VotingPeriodDefaulting)),
                "New block for state slot {StateSlot} is slot {Eth1VotingPeriodSlot} of the Eth1 voting period, but parent root {ParentRoot} is before slot {CheckSlot}, so using the parent's follow distance for start of Eth1 voting period.");
        public static readonly Action<ILogger, BeaconBlock, Exception?> BlockNotAcceptedLocally =
            LoggerMessage.Define<BeaconBlock>(LogLevel.Warning,
                new EventId(4406, nameof(BlockNotAcceptedLocally)),
                "Block {BeaconBlock} not accepted by local chain (but will still try to publish to peers).");
        public static readonly Action<ILogger, Exception?> ApiErrorGetSyncing =
            LoggerMessage.Define(LogLevel.Warning,
                new EventId(4407, nameof(ApiErrorGetSyncing)),
                "Exception result from API get syncing.");
        public static readonly Action<ILogger, Exception?> ApiErrorPublishBlock =
            LoggerMessage.Define(LogLevel.Warning,
                new EventId(4408, nameof(ApiErrorPublishBlock)),
                "Exception result from API publish Block (post).");
        public static readonly Action<ILogger, Epoch, BlsPublicKey, Exception?> ValidatorDoesNotHaveAttestationSlot =
            LoggerMessage.Define<Epoch, BlsPublicKey>(LogLevel.Warning,
                new EventId(4409, nameof(ValidatorDoesNotHaveAttestationSlot)),
                "No attestation slot during epoch {Epoch} for validator {ValidatorPublicKey}.");
        public static readonly Action<ILogger, Epoch, ValidatorIndex, BlsPublicKey, Exception?> ValidatorNotActiveAtEpoch =
            LoggerMessage.Define<Epoch, ValidatorIndex, BlsPublicKey>(LogLevel.Warning,
                new EventId(4410, nameof(ValidatorNotActiveAtEpoch)),
                "No duties as validator not active during epoch {Epoch} for validator {ValidatorIndex}: {ValidatorPublicKey}.");
        public static readonly Action<ILogger, Epoch, BlsPublicKey, Exception?> ValidatorNotFoundAtEpoch =
            LoggerMessage.Define<Epoch, BlsPublicKey>(LogLevel.Warning,
                new EventId(4411, nameof(ValidatorNotFoundAtEpoch)),
                "No duties as validator public key not found at epoch {Epoch} for validator {ValidatorPublicKey}.");
        public static readonly Action<ILogger, Exception?> ApiErrorNewAttestation =
            LoggerMessage.Define(LogLevel.Warning,
                new EventId(4412, nameof(ApiErrorNewAttestation)),
                "Exception result from API Attestation (get).");
        public static readonly Action<ILogger, Attestation, Exception?> AttestationNotAcceptedLocally =
            LoggerMessage.Define<Attestation>(LogLevel.Warning,
                new EventId(4413, nameof(AttestationNotAcceptedLocally)),
                "Attestation {Attestation} not accepted by local chain (but will still try to publish to peers).");
        public static readonly Action<ILogger, Exception?> ApiErrorPublishAttestation =
            LoggerMessage.Define(LogLevel.Warning,
                new EventId(4414, nameof(ApiErrorPublishAttestation)),
                "Exception result from API publish Attestation (post).");

        public static readonly Action<ILogger, long, Exception?> QuickStartClockCreated =
            LoggerMessage.Define<long>(LogLevel.Warning,
                new EventId(4901, nameof(QuickStartClockCreated)),
                "Quick start clock created with offset {ClockOffset:n0}.");
        
        // 5bxx error
        
        public static readonly Action<ILogger, Exception?> BeaconNodeWorkerLoopError =
            LoggerMessage.Define(LogLevel.Error,
                new EventId(5000, nameof(BeaconNodeWorkerLoopError)),
                "Unexpected error caught in beacon node worker, loop continuing.");

        // 8bxx finalization

        // 9bxx critical

        public static readonly Action<ILogger, Exception?> BeaconNodeWorkerCriticalError =
            LoggerMessage.Define(LogLevel.Critical,
                new EventId(9000, nameof(BeaconNodeWorkerCriticalError)),
                "Critical unhandled error in beacon node worker. Worker cannot continue.");
    }
}