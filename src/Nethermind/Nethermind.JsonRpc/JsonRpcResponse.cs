// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Modules.Subscribe;

namespace Nethermind.JsonRpc
{
    [JsonDerivedType(typeof(JsonRpcResponse))]
    [JsonDerivedType(typeof(JsonRpcErrorResponse))]
    [JsonDerivedType(typeof(JsonRpcSubscriptionResponse))]
    public class JsonRpcResponse(Action? action = null) : IDisposable
    {
        private protected JsonRpcId _id;

        internal JsonRpcResponse(in JsonRpcId id, Action? action = null)
            : this(action) => _id = id;

        public void AddDisposable(Action disposableAction) => action += disposableAction;

        [JsonPropertyName("jsonrpc")]
        [JsonPropertyOrder(0)]
        public readonly string JsonRpc = "2.0";

        [JsonConverter(typeof(JsonRpcIdConverter))]
        [JsonPropertyOrder(2)]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public JsonRpcId Id { get => _id; set => _id = value; }

        internal ref readonly JsonRpcId IdRef => ref _id;

        internal virtual bool IsResourceUnavailableError => false;

        internal virtual JsonRpcResponse WithResponseContext(in JsonRpcId id, Action? disposableAction)
        {
            _id = id;
            if (disposableAction is not null) AddDisposable(disposableAction);
            return this;
        }

        internal virtual bool TryGetError(out Error? error)
        {
            error = null;
            return false;
        }

        internal virtual bool TryGetStreamableResult([NotNullWhen(true)] out IStreamableResult? streamable)
        {
            streamable = null;
            return false;
        }

        internal virtual void WriteTo(Utf8JsonWriter writer, JsonSerializerOptions options)
        {
            JsonRpcResponseWriter.WriteEnvelopeStart(writer);
            JsonRpcResponseWriter.WriteEnvelopeEnd(writer, in _id);
        }

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

        public JsonRpcSuccessResponse(Action? disposableAction = null) : base(disposableAction) { }

        internal override bool TryGetStreamableResult([NotNullWhen(true)] out IStreamableResult? streamable) =>
            (streamable = Result as IStreamableResult) is not null;

        internal override void WriteTo(Utf8JsonWriter writer, JsonSerializerOptions options)
        {
            JsonRpcResponseWriter.WriteEnvelopeStart(writer);

            writer.WritePropertyName("result"u8);
            object? result = Result;
            if (result is null)
            {
                writer.WriteNullValue();
            }
            else if (!JsonRpcResponseWriter.TryWriteSimpleObject(writer, result))
            {
                JsonSerializer.Serialize(
                    writer,
                    result,
                    RpcPayloadTypeInfo.Get(options, result.GetType()));
            }

            JsonRpcResponseWriter.WriteEnvelopeEnd(writer, in _id);
        }

        public override void Dispose()
        {
            Result.TryDispose();
            base.Dispose();
        }
    }

    public class JsonRpcErrorResponse : JsonRpcResponse
    {
        [JsonPropertyOrder(1)]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public Error? Error { get; set; }

        [JsonConstructor]
        public JsonRpcErrorResponse() : base(null) { }

        public JsonRpcErrorResponse(Action? disposableAction = null) : base(disposableAction) { }

        internal JsonRpcErrorResponse(in JsonRpcId id, Action? disposableAction = null) : base(in id, disposableAction) { }

        internal override bool IsResourceUnavailableError => Error is { Code: ErrorCodes.ModuleTimeout or ErrorCodes.LimitExceeded };

        internal override bool TryGetError(out Error? error)
        {
            error = Error;
            return true;
        }

        internal override void WriteTo(Utf8JsonWriter writer, JsonSerializerOptions options)
        {
            JsonRpcResponseWriter.WriteEnvelopeStart(writer);

            writer.WritePropertyName("error"u8);
            if (Error is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                JsonRpcResponseWriter.WriteErrorObject(writer, Error, options);
            }

            JsonRpcResponseWriter.WriteEnvelopeEnd(writer, in _id);
        }
    }
}
