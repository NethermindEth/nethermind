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

namespace Nethermind.BeaconNode
{
    internal static class Log
    { 
        // 1bxx preliminary

        // 2bxx completion

        // 3bzzz debug
        internal static readonly Action<ILogger, Slot, BlsSignature, Slot, Exception?> NewBlockSkippedSlots =
            LoggerMessage.Define<Slot, BlsSignature, Slot>(LogLevel.Debug,
                new EventId(32000, nameof(NewBlockSkippedSlots)),
                "Request for new block for slot {Slot} for randao {RandaoReveal} is skipping from parent slot {ParentSlot}.");

        // 4bxx warning

        // 5bxx error

        // 8bxx finalization

        // 9bxx critical


    }
}