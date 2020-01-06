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
                "Beacon Node {ProductTokenVersion} worker started; {Environment} environment (config '{Config}') [{ThreadId}]");
        
        public static readonly Action<ILogger, Hash32, ulong, int, Exception?> InitializeBeaconState =
            LoggerMessage.Define<Hash32, ulong, int>(LogLevel.Information,
                new EventId(1300, nameof(InitializeBeaconState)),
                "Initialise beacon state from ETH1 block {Eth1BlockHash}, time {Eth1Timestamp}, with {DepositCount} deposits.");

        // 2bxx completion
        
        public static readonly Action<ILogger, ulong, ulong, long, int, Exception?> WorkerStoreAvailableTickStarted =
            LoggerMessage.Define<ulong, ulong, long, int>(LogLevel.Information,
                new EventId(2000, nameof(WorkerStoreAvailableTickStarted)),
                "Store available with genesis time {GenesisTime}, at clock time {Time} (slot {SlotValue}), starting clock tick [{ThreadId}]");
        
        public static readonly Action<ILogger, Hash32, BeaconState, Hash32, BeaconBlock, Exception?> ValidatedStateTransition =
            LoggerMessage.Define<Hash32, BeaconState, Hash32, BeaconBlock>(LogLevel.Information,
                new EventId(2100, nameof(ValidatedStateTransition)),
                "Validated state transition to new state root {StateRoot} ({BeaconState}) by block {BlockSigningRoot} ({BeaconBlock})");

        public static readonly Action<ILogger, BeaconBlock, BeaconState, Checkpoint, Hash32, Exception?> CreateGenesisStore =
            LoggerMessage.Define<BeaconBlock, BeaconState, Checkpoint, Hash32>(LogLevel.Information,
                new EventId(2200, nameof(CreateGenesisStore)),
                "Creating genesis store with block {BeaconBlock} for state {BeaconState}, with checkpoint {JustifiedCheckpoint}, with signing root {SigningRoot}");

        public static readonly Action<ILogger, Attestation, Exception?> OnAttestation =
            LoggerMessage.Define<Attestation>(LogLevel.Information,
                new EventId(2201, nameof(OnAttestation)),
                "Fork choice received attestation {Attestation}");

        public static readonly Action<ILogger, Hash32, BeaconBlock, Exception?> OnBlock =
            LoggerMessage.Define<Hash32, BeaconBlock>(LogLevel.Information,
                new EventId(2202, nameof(OnBlock)),
                "Fork choice received block {BlockSigningRoot} ({BeaconBlock})");

        public static readonly Action<ILogger, Epoch, Slot, ulong, Exception?> OnTickNewEpoch =
            LoggerMessage.Define<Epoch, Slot, ulong>(LogLevel.Information,
                new EventId(2203, nameof(OnTickNewEpoch)),
                "Fork choice new epoch {Epoch} at slot {Slot} time {Time:n0}");

        public static readonly Action<ILogger, long, Exception?> GenesisCountdown =
            LoggerMessage.Define<long>(LogLevel.Information,
                new EventId(2204, nameof(GenesisCountdown)),
                "Countdown {Time:n0} seconds to expected genesis.");

        // 4bxx warning

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
        public static readonly Action<ILogger, Slot, Slot, Hash32, Slot, Exception?> NoBlocksSinceEth1VotingPeriodDefaulting =
            LoggerMessage.Define<Slot, Slot, Hash32, Slot>(LogLevel.Warning,
                new EventId(4405, nameof(NoBlocksSinceEth1VotingPeriodDefaulting)),
                "New block for state slot {StateSlot} is slot {Eth1VotingPeriodSlot} of the Eth1 voting period, but parent root {ParentRoot} is before slot {CheckSlot}, so using the parent's follow distance for start of Eth1 voting period.");
        public static readonly Action<ILogger, BeaconBlock, Exception?> BlockNotAcceptedLocally =
            LoggerMessage.Define<BeaconBlock>(LogLevel.Warning,
                new EventId(4406, nameof(BlockNotAcceptedLocally)),
                "Block {BeaconBlock} not accepted by local chain (but will still try to publish to peers).");

        public static readonly Action<ILogger, ulong, ulong, Exception?> MockedQuickStart =
            LoggerMessage.Define<ulong, ulong>(LogLevel.Warning,
                new EventId(4900, nameof(MockedQuickStart)),
                "Mocked quick start with genesis time {GenesisTime:n0} and {ValidatorCount} validators.");

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