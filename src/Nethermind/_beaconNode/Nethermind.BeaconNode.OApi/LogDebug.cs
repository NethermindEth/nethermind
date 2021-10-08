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

namespace Nethermind.BeaconNode.OApi
{
    internal static class LogDebug
    {
        // 64xx debug - validator
        
        public static readonly Action<ILogger, Exception?> NodeGenesisTimeRequested =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6480, nameof(NodeGenesisTimeRequested)),
                "Node genesis time requested.");
        
        public static readonly Action<ILogger, Exception?> NodeForkRequested =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6481, nameof(NodeForkRequested)),
                "Node fork requested.");
        
        public static readonly Action<ILogger, Exception?> NodeSyncingRequested =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6482, nameof(NodeSyncingRequested)),
                "Node syncing status requested.");

        public static readonly Action<ILogger, ulong, ulong, string, Exception?> NewAttestationRequested =
            LoggerMessage.Define<ulong, ulong, string>(LogLevel.Debug,
                new EventId(6483, nameof(NewAttestationRequested)),
                "New attestation requested for slot {AttestationSlot}, index {AttestationIndex}, for validator {ValidatorPublicKey}.");
        
        public static readonly Action<ILogger, Slot?, CommitteeIndex?, string?, BlsSignature?, Exception?>
            AttestationPublished =
                LoggerMessage.Define<Slot?, CommitteeIndex?, string?, BlsSignature?>(LogLevel.Debug,
                    new EventId(6484, nameof(AttestationPublished)),
                    "Attestation received for slot {AttestationSlot}, index {AttestationIndex}, bits {AggregationBits}, with signature {Signature}");
    }
}