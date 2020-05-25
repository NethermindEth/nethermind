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
using Nethermind.Core2.Api;
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

        public static readonly Action<ILogger, string, string, string, int, Exception?> HonestValidatorWorkerExecuteStarted =
            LoggerMessage.Define<string, string, string, int>(LogLevel.Information,
                new EventId(1450, nameof(HonestValidatorWorkerExecuteStarted)),
                "Honest Validator {ProductTokenVersion} worker started; data directory '{DataDirectory}' (environment {Environment}) [{ThreadId}]");

        public static readonly Action<ILogger, string, ulong, Exception?> HonestValidatorWorkerConnected =
            LoggerMessage.Define<string, ulong>(LogLevel.Information,
                new EventId(1452, nameof(HonestValidatorWorkerConnected)),
                "Validator connected to '{NodeVersion}' with genesis time {GenesisTime}.");

        // 2bxx 
        
        public static readonly Action<ILogger, BlsPublicKey, Epoch, Slot, Exception?> ValidatorDutyProposalChanged =
            LoggerMessage.Define<BlsPublicKey, Epoch, Slot>(LogLevel.Information,
                new EventId(2451, nameof(ValidatorDutyProposalChanged)),
                "Validator {PublicKey} epoch {Epoch} duty proposal slot {Slot}.");

        public static readonly Action<ILogger, Slot, ulong, BlsPublicKey, Exception?> ProposalDutyFor =
            LoggerMessage.Define<Slot, ulong, BlsPublicKey>(LogLevel.Information,
                new EventId(2452, nameof(ProposalDutyFor)),
                "Running block proposal duty for slot {Slot} at time {Time} for validator {PublicKey}.");

        // 4bxx warning
        
        public static readonly Action<ILogger, Exception?> WaitingForNodeVersion =
            LoggerMessage.Define(LogLevel.Warning,
                new EventId(4450, nameof(WaitingForNodeVersion)),
                "Waiting for node version to succeed.");
        public static readonly Action<ILogger, Exception?> WaitingForGenesisTime =
            LoggerMessage.Define(LogLevel.Warning,
                new EventId(4451, nameof(WaitingForGenesisTime)),
                "Waiting for node genesis time to succeed.");
        // FIXME: Duplicate of beacon node
        public static readonly Action<ILogger, long, Exception?> QuickStartClockCreated =
            LoggerMessage.Define<long>(LogLevel.Warning,
                new EventId(4901, nameof(QuickStartClockCreated)),
                "Quick start clock created with offset {ClockOffset:n0}.");
        
        // 5bxx error
        
        public static readonly Action<ILogger, Exception?> HonestValidatorWorkerLoopError =
            LoggerMessage.Define(LogLevel.Error,
                new EventId(5450, nameof(HonestValidatorWorkerLoopError)),
                "Unexpected error caught in honest validator worker, loop continuing.");
        public static readonly Action<ILogger, int, StatusCode, Exception?> ErrorGettingValidatorDuties =
            LoggerMessage.Define<int, StatusCode>(LogLevel.Error,
                new EventId(5451, nameof(ErrorGettingValidatorDuties)),
                "Error getting updated validator duties from beacon node, response: {StatusCodeNumeric} {StatusCode}.");
        public static readonly Action<ILogger, int, StatusCode, Exception?> ErrorGettingForkVersion =
            LoggerMessage.Define<int, StatusCode>(LogLevel.Error,
                new EventId(5452, nameof(ErrorGettingForkVersion)),
                "Error getting updated fork version from beacon node, response: {StatusCodeNumeric} {StatusCode}.");
        public static readonly Action<ILogger, int, StatusCode, Exception?> ErrorGettingSyncStatus =
            LoggerMessage.Define<int, StatusCode>(LogLevel.Error,
                new EventId(5453, nameof(ErrorGettingSyncStatus)),
                "Error getting updated sync status from beacon node, response: {StatusCodeNumeric} {StatusCode}.");
        public static readonly Action<ILogger, string, Exception?> ExceptionGettingValidatorDuties =
            LoggerMessage.Define<string>(LogLevel.Error,
                new EventId(5454, nameof(ErrorGettingValidatorDuties)),
                "Exception getting updated validator duties from beacon node, error message: {ErrorMessage}");
        public static readonly Action<ILogger, string, Exception?> ExceptionGettingForkVersion =
            LoggerMessage.Define<string>(LogLevel.Error,
                new EventId(5455, nameof(ErrorGettingForkVersion)),
                "Exception getting updated fork version from beacon node, error message: {ErrorMessage}");
        public static readonly Action<ILogger, string, Exception?> ExceptionGettingSyncStatus =
            LoggerMessage.Define<string>(LogLevel.Error,
                new EventId(5456, nameof(ErrorGettingSyncStatus)),
                "Exception getting updated sync status from beacon node, error message: {ErrorMessage}");
        public static readonly Action<ILogger, Slot, BlsPublicKey, string, Exception?> ExceptionProcessingProposalDuty =
            LoggerMessage.Define<Slot, BlsPublicKey, string>(LogLevel.Error,
                new EventId(5457, nameof(ExceptionProcessingProposalDuty)),
                "Exception processing proposal duty for slot {Slot} for validator public key {ValidatorPublicKey}, error message: {ErrorMessage}");
        public static readonly Action<ILogger, Slot, BlsPublicKey, string, Exception?> ExceptionProcessingAttestationDuty =
            LoggerMessage.Define<Slot, BlsPublicKey, string>(LogLevel.Error,
                new EventId(5458, nameof(ExceptionProcessingAttestationDuty)),
                "Exception processing attestation duty for slot {Slot} for validator public key {ValidatorPublicKey}, error message: {ErrorMessage}");

        // 8bxx finalization

        // 9bxx critical

        public static readonly Action<ILogger, Exception?> HonestValidatorWorkerCriticalError =
            LoggerMessage.Define(LogLevel.Critical,
                new EventId(9450, nameof(HonestValidatorWorkerCriticalError)),
                "Critical unhandled error in honest validator worker. Worker cannot continue.");

    }
}