// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

using Nethermind.Serialization.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Int256;

namespace Nethermind.JsonRpc
{
    [JsonDerivedType(typeof(JsonRpcResponse))]
    [JsonDerivedType(typeof(JsonRpcSuccessResponse))]
    [JsonDerivedType(typeof(JsonRpcErrorResponse))]
    [JsonDerivedType(typeof(JsonRpcSubscriptionResponse))]
    public class JsonRpcResponse : IDisposable
    {
        private Action? _disposableAction;

        public JsonRpcResponse(Action? disposableAction = null)
        {
            _disposableAction = disposableAction;
        }

        public void AddDisposable(Action disposableAction)
        {
            if (_disposableAction is null)
            {
                _disposableAction = disposableAction;
            }
            else
            {
                _disposableAction += disposableAction;
            }
        }

        [JsonPropertyName("jsonrpc")]
        [JsonPropertyOrder(0)]
        public readonly string JsonRpc = "2.0";

        [JsonConverter(typeof(IdConverter))]
        [JsonPropertyName("id")]
        [JsonPropertyOrder(2)]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public object? Id { get; set; }

        [JsonIgnore]
        public string MethodName { get; set; }

        public virtual void Dispose()
        {
            _disposableAction?.Invoke();
            _disposableAction = null;
        }
    }

    public class JsonRpcSuccessResponse : JsonRpcResponse
    {
        [JsonPropertyName("result")]
        [JsonPropertyOrder(1)]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public object? Result { get; set; }

        [JsonConstructor]
        public JsonRpcSuccessResponse() : base(null) { }

        public JsonRpcSuccessResponse(Action? disposableAction = null) : base(disposableAction)
        {
        }

        public override void Dispose()
        {
            Result.TryDispose();
            base.Dispose();
        }
    }

    public class JsonRpcErrorResponse : JsonRpcResponse
    {
        [JsonPropertyName("error")]
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
