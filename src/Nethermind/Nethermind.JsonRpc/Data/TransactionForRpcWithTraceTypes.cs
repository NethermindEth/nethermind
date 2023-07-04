// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nethermind.JsonRpc.Data
{
    public class TransactionForRpcWithTraceTypes
    {
        public TransactionForRpc Transaction { get; set; }
        public string[] TraceTypes { get; set; }
    }


}
