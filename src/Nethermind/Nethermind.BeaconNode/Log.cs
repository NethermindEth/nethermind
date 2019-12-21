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
using Nethermind.Core2.Types;
using Attestation = Nethermind.BeaconNode.Containers.Attestation;
using BeaconBlock = Nethermind.BeaconNode.Containers.BeaconBlock;
using BeaconState = Nethermind.BeaconNode.Containers.BeaconState;

namespace Nethermind.BeaconNode
{
    internal static class Log
    {
        // 1bxx preliminary

        // 2bxx completion

        public static readonly Action<ILogger, Hash32, BeaconState, Hash32, BeaconBlock, Exception?> ValidatedStateTransition =
            LoggerMessage.Define<Hash32, BeaconState, Hash32, BeaconBlock>(LogLevel.Information,
                new EventId(2000, nameof(ValidatedStateTransition)),
                "Validated state transition to new state root {StateRoot} ({BeaconState}) by block {BlockSigningRoot} ({BeaconBlock})");

        public static readonly Action<ILogger, Attestation, Exception?> OnAttestation =
            LoggerMessage.Define<Attestation>(LogLevel.Information,
                new EventId(2100, nameof(OnAttestation)),
                "Fork choice received attestation {Attestation}");

        public static readonly Action<ILogger, Hash32, BeaconBlock, Exception?> OnBlock =
            LoggerMessage.Define<Hash32, BeaconBlock>(LogLevel.Information,
                new EventId(2101, nameof(OnBlock)),
                "Fork choice received block {BlockSigningRoot} ({BeaconBlock})");

        public static readonly Action<ILogger, Epoch, Slot, ulong, Exception?> OnTickNewEpoch =
            LoggerMessage.Define<Epoch, Slot, ulong>(LogLevel.Information,
                new EventId(2101, nameof(OnTickNewEpoch)),
                "Fork choice new epoch {Epoch} at slot {Slot} time {Time:n0}");

        // 4bxx warning

        // 5bxx error

        // 8bxx finalization

        // 9bxx critical



    }
}