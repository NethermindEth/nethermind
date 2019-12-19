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
using Nethermind.BeaconNode.Containers;

namespace Nethermind.BeaconNode
{
    internal static class Log
    { 
        // 1bxx preliminary

        // 2bxx completion
        public static readonly Action<ILogger, bool, BeaconBlock, BeaconState, Exception?> ProcessBlock =
            LoggerMessage.Define<bool, BeaconBlock, BeaconState>(LogLevel.Information,
                new EventId(2005, nameof(ProcessBlock)),
                "Process (validate {Validate}) block {BeaconBlock} for state {BeaconState}");
        public static readonly Action<ILogger, BeaconBlock,  Exception?> ProcessBlockHeader =
            LoggerMessage.Define<BeaconBlock>(LogLevel.Information,
                new EventId(2006, nameof(ProcessBlockHeader)),
                "Process block header for block {BeaconBlock}");
        public static readonly Action<ILogger, AttesterSlashing, Exception?> ProcessAttesterSlashing =
            LoggerMessage.Define<AttesterSlashing>(LogLevel.Information,
                new EventId(2011, nameof(ProcessAttesterSlashing)),
                "Process block operation attester slashing {AttesterSlashing}");
        public static readonly Action<ILogger, Attestation, BeaconState, Exception?> ProcessAttestation =
            LoggerMessage.Define<Attestation, BeaconState>(LogLevel.Information,
                new EventId(2012, nameof(ProcessAttestation)),
                "Process block operation attestation {Attestation} for state {BeaconState}.");

        // 3bxx debug
        public static readonly Action<ILogger, ValidatorIndex, string, Gwei, Exception?> RewardForValidator =
            LoggerMessage.Define<ValidatorIndex, string, Gwei>(LogLevel.Debug,
                new EventId(3001, nameof(RewardForValidator)),
                "Reward for validator {ValidatorIndex}: {RewardName} +{Reward}.");

        public static readonly Action<ILogger, ValidatorIndex, string, Gwei, Exception?> PenaltyForValidator =
            LoggerMessage.Define<ValidatorIndex, string, Gwei>(LogLevel.Debug,
                new EventId(3002, nameof(PenaltyForValidator)),
                "Penalty for validator {ValidatorIndex}: {PenaltyName} -{Penalty}.");


        public static readonly Action<ILogger, Slot, BlsSignature, Slot, Exception?> NewBlockSkippedSlots =
            LoggerMessage.Define<Slot, BlsSignature, Slot>(LogLevel.Debug,
                new EventId(3200, nameof(NewBlockSkippedSlots)),
                "Request for new block for slot {Slot} for randao {RandaoReveal} is skipping from parent slot {ParentSlot}.");

        // 4bxx warning

        // 5bxx error

        // 8bxx finalization

        // 9bxx critical


    }
}