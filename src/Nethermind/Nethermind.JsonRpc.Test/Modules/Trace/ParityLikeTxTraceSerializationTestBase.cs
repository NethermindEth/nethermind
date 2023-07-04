// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
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
            ParityTraceAction subtrace = new()
            {
                Value = 67890,
                CallType = "call",
                From = TestItem.AddressC,
                To = TestItem.AddressD,
                Input = Array.Empty<byte>(),
                Gas = 10000,
                TraceAddress = new int[] { 0, 0 }
            };

            ParityLikeTxTrace result = new()
            {
                Action = new ParityTraceAction
                {
                    Value = 12345,
                    CallType = "init",
                    From = TestItem.AddressA,
                    To = TestItem.AddressB,
                    Input = new byte[] { 1, 2, 3, 4, 5, 6 },
                    Gas = 40000,
                    TraceAddress = new int[] { 0 }
                },
                BlockHash = TestItem.KeccakB,
                BlockNumber = 123456,
                TransactionHash = TestItem.KeccakC,
                TransactionPosition = 5
            };
            result.Action.TraceAddress = new int[] { 1, 2, 3 };
            result.Action.Subtraces.Add(subtrace);

            ParityAccountStateChange stateChange = new()
            {
                Balance = new ParityStateChange<UInt256?>(1, 2),
                Nonce = new ParityStateChange<UInt256?>(0, 1),
                Storage = new Dictionary<UInt256, ParityStateChange<byte[]>> { [1] = new(new byte[] { 1 }, new byte[] { 2 }) },
                Code = new ParityStateChange<byte[]>(new byte[] { 1 }, new byte[] { 2 })
            };

            result.StateChanges = new Dictionary<Address, ParityAccountStateChange> { { TestItem.AddressC, stateChange } };
            return result;
        }
    }
}
