// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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

        internal virtual bool HasDisposableResources => action is not null;
        internal virtual bool IsResourceUnavailableError => false;

        internal virtual JsonRpcResponse WithResponseContext(JsonRpcId id, Action? disposableAction)
        {
            Id = id;
            if (disposableAction is not null)
            {
                AddDisposable(disposableAction);
            }

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
            JsonRpcResponseWriter.WriteEnvelopeEnd(writer, Id);
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

        public JsonRpcSuccessResponse(Action? disposableAction = null) : base(disposableAction)
        {
        }

        internal override bool HasDisposableResources =>
            Result is IDisposable ||
            Result is ITuple tuple && HasDisposableItem(tuple) ||
            base.HasDisposableResources;

        internal override bool TryGetStreamableResult([NotNullWhen(true)] out IStreamableResult? streamable)
        {
            streamable = Result as IStreamableResult;
            return streamable is not null;
        }

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

            JsonRpcResponseWriter.WriteEnvelopeEnd(writer, Id);
        }

        public override void Dispose()
        {
            if (HasDisposableResources)
            {
                Result.TryDispose();
            }

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

            JsonRpcResponseWriter.WriteEnvelopeEnd(writer, Id);
        }
    }
}
