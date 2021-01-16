//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.JsonRpc.Test.Data;

namespace Nethermind.JsonRpc.Test.Modules.Trace
{
    public class ParityLikeTxTraceSerializationTestBase : SerializationTestBase
    {
        protected static ParityLikeTxTrace BuildParityTxTrace()
        {
            ParityTraceAction subtrace = new ParityTraceAction
            {
                Value = 67890,
                CallType = "call",
                From = TestItem.AddressC,
                To = TestItem.AddressD,
                Input = Array.Empty<byte>(),
                Gas = 10000,
                TraceAddress = new int[] {0, 0}
            };

            ParityLikeTxTrace result = new ParityLikeTxTrace
            {
                Action = new ParityTraceAction
                {
                    Value = 12345,
                    CallType = "init",
                    From = TestItem.AddressA,
                    To = TestItem.AddressB,
                    Input = new byte[] {1, 2, 3, 4, 5, 6},
                    Gas = 40000,
                    TraceAddress = new int[] {0}
                },
                BlockHash = TestItem.KeccakB,
                BlockNumber = 123456,
                TransactionHash = TestItem.KeccakC,
                TransactionPosition = 5
            };
            result.Action.TraceAddress = new int[] {1, 2, 3};
            result.Action.Subtraces.Add(subtrace);

            ParityAccountStateChange stateChange = new ParityAccountStateChange
            {
                Balance = new ParityStateChange<UInt256?>(1, 2),
                Nonce = new ParityStateChange<UInt256?>(0, 1),
                Storage = new Dictionary<UInt256, ParityStateChange<byte[]>> {[1] = new ParityStateChange<byte[]>(new byte[] {1}, new byte[] {2})},
                Code = new ParityStateChange<byte[]>(new byte[] {1}, new byte[] {2})
            };

            result.StateChanges = new Dictionary<Address, ParityAccountStateChange> {{TestItem.AddressC, stateChange}};
            return result;
        }
    }
}
