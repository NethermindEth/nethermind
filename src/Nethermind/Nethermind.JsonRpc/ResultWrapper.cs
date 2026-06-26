// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Facade.Proxy;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.JsonRpc
{
    public class ResultWrapper<T> : JsonRpcResponse, IResultWrapper
    {
        object? IResultWrapper.Data => Data;
        public T Data { get; init; } = default!;
        public Result Result { get; init; } = Result.Success;
        public int ErrorCode { get; init; }
        public bool IsTemporary { get; init; }
        public virtual bool HasErrorData { get; init; }

        protected ResultWrapper()
        {
        }

        public static ResultWrapper<T> Fail<TSearch>(SearchResult<TSearch> searchResult, bool isTemporary = false) where TSearch : class =>
            new() { Result = Result.Fail(searchResult.Error!), ErrorCode = searchResult.ErrorCode, IsTemporary = isTemporary };

        public static ResultWrapper<T> Fail(string error) =>
            new() { Result = Result.Fail(error), ErrorCode = ErrorCodes.InternalError };

        public static ResultWrapper<T> Fail(string error, int errorCode, T outputData) =>
            new() { Result = Result.Fail(error), ErrorCode = errorCode, Data = outputData, HasErrorData = true };

        public static ResultWrapper<T> Fail(string error, int errorCode, bool isTemporary = false) =>
            new() { Result = Result.Fail(error), ErrorCode = errorCode, IsTemporary = isTemporary };

        public static ResultWrapper<T> Success(T data) =>
            new() { Data = data, Result = Result.Success };

        public static ResultWrapper<T> From(RpcResult<T>? rpcResult) =>
            rpcResult is null
                ? Fail("Missing result.")
                : rpcResult.IsValid ? Success(rpcResult.Result!) : Fail(rpcResult.Error?.Message ?? "Missing result.");

        public static ResultWrapper<T> From(IResultWrapper source, T? data = default) => new()
        {
            Data = data ?? (source.Data is T sourceData ? sourceData : default!),
            Result = source.Result,
            ErrorCode = source.ErrorCode,
            IsTemporary = source.IsTemporary,
            HasErrorData = source.HasErrorData,
        };

        public static implicit operator Task<ResultWrapper<T>>(ResultWrapper<T> resultWrapper) => Task.FromResult(resultWrapper);

        internal override bool IsResourceUnavailableError =>
            Result.ResultType != ResultType.Success &&
            ErrorCode is ErrorCodes.ModuleTimeout or ErrorCodes.LimitExceeded;

        internal override JsonRpcResponse WithResponseContext(in JsonRpcId id, Action? disposableAction)
        {
            ResultWrapper<T> response = (ResultWrapper<T>)MemberwiseClone();
            response._id = id;
            if (disposableAction is not null) response.AddDisposable(disposableAction);
            return response;
        }

        internal override bool TryGetError(out Error? error)
        {
            if (Result.ResultType == ResultType.Success)
            {
                error = null;
                return false;
            }

            error = new Error
            {
                Code = ErrorCode,
                Message = Result.Error,
                SuppressWarning = IsTemporary
            };
            return true;
        }

        internal override bool TryGetStreamableResult([NotNullWhen(true)] out IStreamableResult? streamable)
        {
            streamable = Result.ResultType == ResultType.Success && RpcPayloadTypeShape<T>.CanBeStreamable
                ? Data as IStreamableResult
                : null;
            return streamable is not null;
        }

        internal override void WriteTo(Utf8JsonWriter writer, JsonSerializerOptions options)
        {
            JsonRpcResponseWriter.WriteEnvelopeStart(writer);

            if (Result.ResultType == ResultType.Success)
            {
                writer.WritePropertyName("result"u8);
                WritePayloadValue(writer, options, Data);
            }
            else
            {
                WriteError(writer, options);
            }

            JsonRpcResponseWriter.WriteEnvelopeEnd(writer, in _id);
        }

        public override void Dispose()
        {
            DisposePayloads();
            base.Dispose();
        }

        protected virtual void DisposePayloads() => DisposeIfReferenceType(Data);

        protected virtual void WriteErrorData(Utf8JsonWriter writer, JsonSerializerOptions options) =>
            WritePayloadValueCore(writer, options, Data, rejectStreamable: false);

        private void WriteError(Utf8JsonWriter writer, JsonSerializerOptions options)
        {
            writer.WritePropertyName("error"u8);
            writer.WriteStartObject();
            writer.WriteNumber("code"u8, ErrorCode);
            writer.WriteString("message"u8, Result.Error);

            if (HasErrorData)
            {
                writer.WritePropertyName("data"u8);
                WriteErrorData(writer, options);
            }

            writer.WriteEndObject();
        }

        protected static void WritePayloadValue(Utf8JsonWriter writer, JsonSerializerOptions options, T value) =>
            WritePayloadValueCore(writer, options, value, rejectStreamable: true);

        protected static void WritePayloadValueCore<TValue>(Utf8JsonWriter writer, JsonSerializerOptions options, TValue value, bool rejectStreamable)
        {
            if (value is null)
            {
                writer.WriteNullValue();
                return;
            }

            if (rejectStreamable && RpcPayloadTypeShape<TValue>.CanBeStreamable && value is IStreamableResult)
            {
                throw new InvalidOperationException("Streamable JSON-RPC results must be written by the response sink.");
            }

            if (!JsonRpcResponseWriter.TryWriteSimpleValue(writer, value))
            {
                JsonTypeInfo? runtimeTypeInfo = GetRuntimePayloadTypeInfo(options, value);
                if (runtimeTypeInfo is not null)
                {
                    JsonSerializer.Serialize(writer, (object?)value, runtimeTypeInfo);
                    return;
                }

                JsonSerializer.Serialize(writer, value, RpcPayloadTypeInfo<TValue>.Get(options));
            }
        }

        private static JsonTypeInfo? GetRuntimePayloadTypeInfo<TValue>(JsonSerializerOptions options, TValue value)
        {
            if (!RpcPayloadTypeShape<TValue>.CanHaveDerivedRuntimeType)
            {
                return null;
            }

            Type runtimeType = value!.GetType();
            return runtimeType == typeof(TValue) ? null : RpcPayloadTypeInfo.Get(options, runtimeType);
        }

        protected static void DisposeIfReferenceType<TValue>(TValue value)
        {
            if (!typeof(TValue).IsValueType) ((object?)value).TryDispose();
        }
    }

    public class ResultWrapper<T, TErrorData> : ResultWrapper<T>, IResultWrapper
    {
        public TErrorData ErrorData { get; init; } = default!;

        object? IResultWrapper.Data => ErrorData;

        public override bool HasErrorData { get; init; }

        private ResultWrapper()
        {
        }

        public static ResultWrapper<T, TErrorData> Fail(string error, int errorCode, TErrorData errorData) =>
            new() { ErrorCode = errorCode, ErrorData = errorData, Result = Result.Fail(error), HasErrorData = true };

        public new static ResultWrapper<T, TErrorData> Success(T data) =>
            new() { Data = data, ErrorData = default!, Result = Result.Success };

        public static implicit operator Task<ResultWrapper<T, TErrorData>>(ResultWrapper<T, TErrorData> resultWrapper) => Task.FromResult(resultWrapper);

        protected override void DisposePayloads()
        {
            base.DisposePayloads();
            DisposeIfReferenceType(ErrorData);
        }

        protected override void WriteErrorData(Utf8JsonWriter writer, JsonSerializerOptions options) =>
            WritePayloadValueCore(writer, options, ErrorData, rejectStreamable: false);
    }
}
