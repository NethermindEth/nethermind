// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Merge.Plugin.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Data
{
    public class ExecutionPayloadForRpc(string methodName, ExecutionPayload executionPayload)
    {
        public string MethodName { get; } = methodName ?? throw new ArgumentNullException(nameof(methodName));
        public ExecutionPayload ExecutionPayload { get; } = executionPayload ?? throw new ArgumentNullException(nameof(executionPayload));
        public override string ToString() => $"{{MethodName: {MethodName}, ExecutionPayload: {ExecutionPayload}}}";
    }
}
