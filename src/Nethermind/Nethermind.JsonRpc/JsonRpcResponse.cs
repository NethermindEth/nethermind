// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

using Nethermind.Serialization.Json;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc
{
    [System.Text.Json.Serialization.JsonDerivedType(typeof(JsonRpcResponse))]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(JsonRpcSuccessResponse))]
    [System.Text.Json.Serialization.JsonDerivedType(typeof(JsonRpcErrorResponse))]
    public class JsonRpcResponse : IDisposable
    {
        private Action? _disposableAction;

        public JsonRpcResponse(Action? disposableAction = null)
        {
            _disposableAction = disposableAction;
        }

        [JsonProperty(PropertyName = "jsonrpc", Order = 0)]
        [System.Text.Json.Serialization.JsonPropertyName("jsonrpc")]
        [System.Text.Json.Serialization.JsonPropertyOrder(0)]
        public readonly string JsonRpc = "2.0";

        [JsonConverter(typeof(IdConverter))]
        [JsonProperty(PropertyName = "id", Order = 2, NullValueHandling = NullValueHandling.Include)]
        [System.Text.Json.Serialization.JsonConverter(typeof(IdJsonConverter))]
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        [System.Text.Json.Serialization.JsonPropertyOrder(2)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
        public object? Id { get; set; }

        [JsonIgnore]
        [System.Text.Json.Serialization.JsonIgnore]
        public string MethodName { get; set; }

        public void Dispose()
        {
            _disposableAction?.Invoke();
            _disposableAction = null;
        }
    }

    public class JsonRpcSuccessResponse : JsonRpcResponse
    {
        [JsonProperty(PropertyName = "result", NullValueHandling = NullValueHandling.Include, Order = 1)]
        [System.Text.Json.Serialization.JsonPropertyName("result")]
        [System.Text.Json.Serialization.JsonPropertyOrder(1)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
        public object? Result { get; set; }

        public JsonRpcSuccessResponse(Action? disposableAction = null) : base(disposableAction)
        {
        }
    }

    public class JsonRpcErrorResponse : JsonRpcResponse
    {
        [JsonProperty(PropertyName = "error", NullValueHandling = NullValueHandling.Include, Order = 1)]
        [System.Text.Json.Serialization.JsonPropertyName("error")]
        [System.Text.Json.Serialization.JsonPropertyOrder(1)]
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
        public Error? Error { get; set; }

        public JsonRpcErrorResponse(Action? disposableAction = null) : base(disposableAction)
        {
        }
    }
}
