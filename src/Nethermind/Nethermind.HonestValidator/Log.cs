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
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.HonestValidator
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

        public static readonly Action<ILogger, string, string, int, Exception?> HonestValidatorWorkerExecuteStarted =
            LoggerMessage.Define<string, string, int>(LogLevel.Information,
                new EventId(1450, nameof(HonestValidatorWorkerExecuteStarted)),
                "Honest Validator {ProductTokenVersion} worker started; {Environment} environment [{ThreadId}]");

        public static readonly Action<ILogger, string, int, Exception?> NodeConnectionSuccess =
            LoggerMessage.Define<string, int>(LogLevel.Warning,
                new EventId(1451, nameof(NodeConnectionFailed)),
                "Connected to node '{NodeUrl}' (index {NodeUrlIndex}).");

        public static readonly Action<ILogger, string, ulong, Exception?> HonestValidatorWorkerConnected =
            LoggerMessage.Define<string, ulong>(LogLevel.Information,
                new EventId(1452, nameof(HonestValidatorWorkerConnected)),
                "Validator connected to '{NodeVersion}' with genesis time {GenesisTime}.");

        // 2bxx 
        
        public static readonly Action<ILogger, BlsPublicKey, Epoch, Slot, Shard, Exception?> ValidatorDutyAttestationChanged =
            LoggerMessage.Define<BlsPublicKey, Epoch, Slot, Shard>(LogLevel.Information,
                new EventId(2450, nameof(ValidatorDutyAttestationChanged)),
                "Validator {PublicKey} epoch {Epoch} duty attestation slot {Slot} for shard {Shard}.");

        public static readonly Action<ILogger, BlsPublicKey, Epoch, Slot, Exception?> ValidatorDutyProposalChanged =
            LoggerMessage.Define<BlsPublicKey, Epoch, Slot>(LogLevel.Information,
                new EventId(2451, nameof(ValidatorDutyProposalChanged)),
                "Validator {PublicKey} epoch {Epoch} duty proposal slot {Slot}.");

        // 4bxx warning
        
        // FIXME: Duplicate of beacon node
        public static readonly Action<ILogger, long, Exception?> QuickStartClockCreated =
            LoggerMessage.Define<long>(LogLevel.Warning,
                new EventId(4901, nameof(QuickStartClockCreated)),
                "Quick start clock created with offset {ClockOffset:n0}.");
        
        public static readonly Action<ILogger, string, Exception?> NodeConnectionFailed =
            LoggerMessage.Define<string>(LogLevel.Warning,
                new EventId(4450, nameof(NodeConnectionFailed)),
                "Connection to '{NodeUrl}' failed. Attempting reconnection.");
        
        public static readonly Action<ILogger, int, int, Exception?> AllNodeConnectionsFailing =
            LoggerMessage.Define<int, int>(LogLevel.Warning,
                new EventId(4451, nameof(AllNodeConnectionsFailing)),
                "All node connections failing (configured with {NodeUrlCount} URLs). Waiting {MillisecondsDelay} milliseconds before attempting reconnection.");

        // 5bxx error
        
        public static readonly Action<ILogger, Exception?> HonestValidatorWorkerLoopError =
            LoggerMessage.Define(LogLevel.Error,
                new EventId(5450, nameof(HonestValidatorWorkerLoopError)),
                "Unexpected error caught in honest validator worker, loop continuing.");

        // 8bxx finalization

        // 9bxx critical

        public static readonly Action<ILogger, Exception?> HonestValidatorWorkerCriticalError =
            LoggerMessage.Define(LogLevel.Critical,
                new EventId(9450, nameof(HonestValidatorWorkerCriticalError)),
                "Critical unhandled error in honest validator worker. Worker cannot continue.");

    }
}