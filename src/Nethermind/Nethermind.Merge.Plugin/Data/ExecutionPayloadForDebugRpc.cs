// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Data
{
    public class Params(ExecutionPayload executionPayload, byte[]?[]? blobVersionedHashes = null, Hash256? parentBeaconBlockRoot = null, byte[][]? executionRequests = null)
    {
        public ExecutionPayload ExecutionPayload { get; set; } = executionPayload ?? throw new ArgumentNullException(nameof(executionPayload));
        public byte[]?[]? BlobVersionedHashes { get; set; } = blobVersionedHashes;
        public Hash256? ParentBeaconBlockRoot { get; set; } = parentBeaconBlockRoot;
        public byte[][]? ExecutionRequests { get; set;  } = executionRequests;
    }
    public class ExecutionPayloadForDebugRpc(string methodName, Params parameters)
    {
        public string MethodName { get; } = methodName ?? throw new ArgumentNullException(nameof(methodName));
        public Params Params { get; } = parameters ?? throw new ArgumentNullException(nameof(parameters));
        public override string ToString() => $"{{MethodName: {MethodName}, ExecutionPayload: {parameters}}}";
    }
}
