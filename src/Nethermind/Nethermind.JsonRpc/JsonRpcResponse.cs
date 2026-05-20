// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
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

        [JsonIgnore]
        internal Type? ResultStaticType { get; set; }

        [JsonIgnore]
        internal Func<JsonSerializerOptions, JsonTypeInfo>? ResultTypeInfoAccessor { get; set; }

        [JsonConstructor]
        public JsonRpcSuccessResponse() : base(null) { }

        public JsonRpcSuccessResponse(Action? disposableAction = null) : base(disposableAction)
        {
        }

        internal override bool HasDisposableResources =>
            Result is IDisposable ||
            Result is ITuple tuple && HasDisposableItem(tuple) ||
            base.HasDisposableResources;

        /// <summary>
        /// Gets static result metadata when it is safe to serialize with the RPC method's declared result type.
        /// </summary>
        /// <remarks>
        /// Returns <see langword="false"/> when the runtime result type differs from the declared type, preserving
        /// existing polymorphic serialization behavior.
        /// </remarks>
        public bool TryGetResultTypeInfo(object result, JsonSerializerOptions options, [NotNullWhen(true)] out JsonTypeInfo? typeInfo)
        {
            Type? staticType = ResultStaticType;
            Func<JsonSerializerOptions, JsonTypeInfo>? accessor = ResultTypeInfoAccessor;
            if (staticType is not null &&
                accessor is not null &&
                CanUseStaticTypeInfo(staticType, result.GetType()))
            {
                typeInfo = accessor(options);
                return true;
            }

            typeInfo = null;
            return false;
        }

        public override void Dispose()
        {
            Result.TryDispose();
            base.Dispose();
        }

        private static bool CanUseStaticTypeInfo(Type staticType, Type runtimeType)
        {
            if (staticType.IsValueType)
            {
                return Nullable.GetUnderlyingType(staticType) is null && runtimeType == staticType;
            }

            return runtimeType == staticType;
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

    internal static class JsonRpcSuccessResponseMetadata<T>
    {
        public static readonly Func<JsonSerializerOptions, JsonTypeInfo> Accessor = GetTypeInfo;
        private static readonly ConcurrentDictionary<JsonSerializerOptions, JsonTypeInfo> _cache = new();

        private static JsonTypeInfo GetTypeInfo(JsonSerializerOptions options) =>
            _cache.GetOrAdd(options, static options => options.GetTypeInfo(typeof(T)));
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
