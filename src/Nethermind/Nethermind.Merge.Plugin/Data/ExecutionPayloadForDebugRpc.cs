// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Data
{
    [JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
    [JsonDerivedType(typeof(ParamsV1), typeDiscriminator: "v1")]
    [JsonDerivedType(typeof(ParamsV3), typeDiscriminator: "v3")]
    [JsonDerivedType(typeof(ParamsV4), typeDiscriminator: "v4")]
    public class Params;
    public class ParamsV1(ExecutionPayload executionPayload) : Params
    {
        public ExecutionPayload ExecutionPayload { get; set; } = executionPayload ?? throw new ArgumentNullException(nameof(executionPayload));
    }
    public class ParamsV3(ExecutionPayload executionPayload, byte[]?[]? blobVersionedHashes = null, Hash256? parentBeaconBlockRoot = null)
        : ParamsV1(executionPayload)
    {
        public byte[]?[]? BlobVersionedHashes { get; set; } = blobVersionedHashes;
        public Hash256? ParentBeaconBlockRoot { get; set; } = parentBeaconBlockRoot;
    }
    public class ParamsV4(ExecutionPayload executionPayload, byte[]?[]? blobVersionedHashes = null, Hash256? parentBeaconBlockRoot = null, byte[][]? executionRequests = null)
        : ParamsV3(executionPayload, blobVersionedHashes, parentBeaconBlockRoot)
    {
        public byte[][]? ExecutionRequests { get; set;  } = executionRequests;
    }



    public class ExecutionPayloadForDebugRpc(string methodName, Params parameters)
    {
        public string MethodName { get; } = methodName ?? throw new ArgumentNullException(nameof(methodName));
        public Params Params { get; } = parameters ?? throw new ArgumentNullException(nameof(parameters));
        public override string ToString() => $"{{MethodName: {MethodName}, ExecutionPayload: {parameters}}}";
    }
}
