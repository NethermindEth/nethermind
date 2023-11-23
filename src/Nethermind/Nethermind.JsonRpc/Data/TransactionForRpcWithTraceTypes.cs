// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.JsonRpc.Data
{
    public class TransactionForRpcWithTraceTypes
    {
        public TransactionForRpc Transaction { get; set; }
        public string[] TraceTypes { get; set; }
    }


}
