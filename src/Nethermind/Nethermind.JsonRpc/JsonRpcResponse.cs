// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Modules.Subscribe;

namespace Nethermind.JsonRpc
{
    [JsonDerivedType(typeof(JsonRpcResponse))]
    [JsonDerivedType(typeof(JsonRpcSuccessResponse))]
    [JsonDerivedType(typeof(JsonRpcErrorResponse))]
    [JsonDerivedType(typeof(JsonRpcSubscriptionResponse))]
    public class JsonRpcResponse(Action? action = null) : IDisposable
    {
        public void AddDisposable(Action disposableAction)
        {
            if (action is null)
            {
                action = disposableAction;
            }
            else
            {
                action += disposableAction;
            }
        }

        [JsonPropertyName("jsonrpc")]
        [JsonPropertyOrder(0)]
        public readonly string JsonRpc = "2.0";

        [JsonConverter(typeof(JsonRpcIdConverter))]
        [JsonPropertyOrder(2)]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public JsonRpcId Id { get; set; }

        [JsonIgnore]
        public string MethodName { get; set; }

        [JsonIgnore]
        public RpcBoundaryTimings BoundaryTimings { get; set; }

        internal virtual bool HasDisposableResources => action is not null;

        public virtual void Dispose()
        {
            action?.Invoke();
            action = null;
        }
    }

    public class JsonRpcSuccessResponse : JsonRpcResponse
    {
        [JsonPropertyOrder(1)]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public object? Result { get; set; }

        [JsonConstructor]
        public JsonRpcSuccessResponse() : base(null) { }

        public JsonRpcSuccessResponse(Action? disposableAction = null) : base(disposableAction)
        {
        }

        internal override bool HasDisposableResources =>
            Result is IDisposable ||
            Result is ITuple tuple && HasDisposableItem(tuple) ||
            base.HasDisposableResources;

        public override void Dispose()
        {
            Result.TryDispose();
            base.Dispose();
        }

        private static bool HasDisposableItem(ITuple tuple)
        {
            for (int i = 0; i < tuple.Length; i++)
            {
                if (tuple[i] is IDisposable)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public class JsonRpcErrorResponse : JsonRpcResponse
    {
        [JsonPropertyOrder(1)]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public Error? Error { get; set; }

        [JsonConstructor]
        public JsonRpcErrorResponse() : base(null) { }

        public JsonRpcErrorResponse(Action? disposableAction = null) : base(disposableAction)
        {
        }
    }
}
