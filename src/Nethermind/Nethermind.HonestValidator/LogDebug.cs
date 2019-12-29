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
using Nethermind.Core2.Types;

namespace Nethermind.HonestValidator
{
    internal static class LogDebug
    { 
        // 6bxx debug

        // 64xx debug - validator
        public static readonly Action<ILogger, Exception?> HonestValidatorWorkerStarting =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6450, nameof(HonestValidatorWorkerStarting)),
                "Honest validator worker starting.");
        public static readonly Action<ILogger, Exception?> HonestValidatorWorkerStarted =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6451, nameof(HonestValidatorWorkerStarted)),
                "Honest validator worker started.");
        public static readonly Action<ILogger, Exception?> HonestValidatorWorkerStopping =
            LoggerMessage.Define(LogLevel.Debug,
                new EventId(6452, nameof(HonestValidatorWorkerStopping)),
                "Honest validator worker stopping.");
        public static readonly Action<ILogger, int, Exception?> HonestValidatorWorkerExecuteExiting =
            LoggerMessage.Define<int>(LogLevel.Debug,
                new EventId(6453, nameof(HonestValidatorWorkerExecuteExiting)),
                "Honest validator worker execute thread exiting [{ThreadId}].");
        public static readonly Action<ILogger, string, int, Exception?> AttemptingConnectionToNode =
            LoggerMessage.Define<string, int>(LogLevel.Debug,
                new EventId(6454, nameof(AttemptingConnectionToNode)),
                "Attempting connection to node '{NodeUrl}' (index {NodeUrlIndex}).");
        public static readonly Action<ILogger, Slot, string, string, Exception?> RequestingBlock =
            LoggerMessage.Define<Slot, string, string>(LogLevel.Debug,
                new EventId(6454, nameof(RequestingBlock)),
                "Requesting new block for slot {Slot} for validator {PublicKey} with RANDAO reveal {RandaoReveal}.");
        public static readonly Action<ILogger, Slot, string, string, Nethermind.BeaconNode.Containers.BeaconBlock, string, Exception?> PublishingSignedBlock =
            LoggerMessage.Define<Slot, string, string, Nethermind.BeaconNode.Containers.BeaconBlock, string>(LogLevel.Debug,
                new EventId(6454, nameof(PublishingSignedBlock)),
                "Publishing signed block for slot {Slot} for validator {PublicKey} with RANDAO reveal {RandaoReveal}, block details {BeaconBlock}, and signature {Signature}.");
    }
}