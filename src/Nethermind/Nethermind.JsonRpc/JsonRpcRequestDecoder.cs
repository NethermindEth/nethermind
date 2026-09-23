// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Nethermind.JsonRpc;

/// <summary>Turns request bytes into a <see cref="JsonRpcRequest"/>. Decoding only - nothing here executes a request.</summary>
/// <remarks>
/// The distinction matters because of <see cref="IsRequestDecodingException"/>: a bare
/// <see cref="InvalidOperationException"/> means "the caller sent bytes we cannot decode" inside this class and
/// "the node is broken" outside it, so the two must not share a <c>catch</c>. Keeping every decode step behind one
/// type boundary makes that a structural property rather than a convention each call site has to restate.
/// </remarks>
internal static class JsonRpcRequestDecoder
{
    private static readonly SearchValues<byte> JsonWhitespace = SearchValues.Create(" \t\r\n"u8);

    private static readonly JsonReaderOptions SocketJsonReaderOptions = new() { AllowMultipleValues = true };

    /// <summary>Tells whether <paramref name="exception"/> means the request text could not be decoded into a JSON-RPC envelope.</summary>
    /// <remarks>
    /// System.Text.Json reports malformed syntax as <see cref="JsonException"/>, but invalid UTF-8 bytes and lone UTF-16
    /// surrogate escapes inside a string value, as well as reading a non-object element as an object, surface as a bare
    /// <see cref="InvalidOperationException"/>. Both are caller input errors and must become a JSON-RPC error response
    /// instead of escaping to the transport as an unhandled exception.
    /// <para>
    /// This is only sound while the guarded scope is a decode step from this class and nothing else. Widening a
    /// <c>catch</c> that also covers request <em>execution</em> to <see cref="InvalidOperationException"/> would
    /// swallow a module-side <see cref="InvalidOperationException"/> or <see cref="ObjectDisposedException"/> and
    /// report it to the caller as -32700 parse error, hiding a real node fault behind a client error.
    /// <see cref="ObjectDisposedException"/> derives from <see cref="InvalidOperationException"/> and so matches
    /// here, which is correct within the decode-only scope.
    /// </para>
    /// </remarks>
    public static bool IsRequestDecodingException(Exception exception) =>
        exception is JsonException or InvalidOperationException;

    /// <summary>Reads <paramref name="memory"/> as a single complete JSON-RPC request object.</summary>
    /// <returns><c>false</c> if the body is not exactly one JSON object, in which case the caller must parse it another way.</returns>
    public static bool TryReadSingleObjectRequest(
        ReadOnlyMemory<byte> memory,
        [NotNullWhen(true)] out JsonRpcRequest? request)
    {
        request = null;

        return TryGetSingleDocumentBody(memory, JsonTokenType.StartObject, out ReadOnlyMemory<byte> objectBody)
            && TryReadObjectRequest(objectBody, out request);
    }

    /// <summary>
    /// Narrows <paramref name="memory"/> to exactly one complete JSON document whose root is
    /// <paramref name="expectedRootToken"/>, rejecting leading or trailing non-whitespace.
    /// </summary>
    public static bool TryGetSingleDocumentBody(
        ReadOnlyMemory<byte> memory,
        JsonTokenType expectedRootToken,
        out ReadOnlyMemory<byte> documentBody)
    {
        documentBody = default;

        ReadOnlyMemory<byte> body = memory[CountLeadingJsonWhitespace(memory.Span)..];
        if (body.IsEmpty)
        {
            return false;
        }

        Utf8JsonReader reader = new(body.Span, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != expectedRootToken)
        {
            return false;
        }

        reader.Skip();
        int documentLength = checked((int)reader.BytesConsumed);
        if (HasNonWhitespace(body.Span[documentLength..]))
        {
            return false;
        }

        documentBody = body[..documentLength];
        return true;
    }

    /// <summary>Reads one JSON object body as a request, keeping <c>params</c> as a slice of <paramref name="objectBody"/>.</summary>
    public static bool TryReadObjectRequest(
        ReadOnlyMemory<byte> objectBody,
        [NotNullWhen(true)] out JsonRpcRequest? request)
    {
        request = null;

        JsonRpcEnvelopeReader envelopeReader = new(objectBody.Span);
        if (!envelopeReader.TryRead(out JsonRpcEnvelope envelope))
        {
            return false;
        }

        ReadOnlyMemory<byte> paramsUtf8 = envelope.HasParams
            ? objectBody.Slice(envelope.ParamsStart, envelope.ParamsLength)
            : default;

        request = CreateRequest(envelope, paramsElement: default, paramsUtf8);
        return true;
    }

    /// <summary>Reads an already-parsed JSON object as a request.</summary>
    public static JsonRpcRequest CreateRequest(JsonElement element)
    {
        JsonRpcEnvelope envelope = JsonRpcEnvelopeReader.Read(element, out JsonElement paramsElement);
        return CreateRequest(envelope, paramsElement, paramsUtf8: default);
    }

    /// <summary>Parses the next complete JSON document out of <paramref name="buffer"/>, advancing it past what was read.</summary>
    /// <remarks>
    /// On success the reader state is reset for the next document; on failure it is preserved so the parse can resume
    /// when more data arrives.
    /// </remarks>
    public static bool TryParseJson(
        ref ReadOnlySequence<byte> buffer,
        bool isFinalBlock,
        ref JsonReaderState readerState,
        [NotNullWhen(true)] out JsonDocument? jsonDocument,
        JsonRpcProcessingOptions options)
    {
        Utf8JsonReader jsonReader = new(buffer, isFinalBlock, readerState);
        bool parsed = JsonDocument.TryParseValue(ref jsonReader, out jsonDocument);
        buffer = buffer.Slice(jsonReader.BytesConsumed);
        readerState = parsed
            ? CreateJsonReaderState(options)
            : jsonReader.CurrentState;

        return parsed;
    }

    public static JsonReaderState CreateJsonReaderState(JsonRpcProcessingOptions options) =>
        new(options.InputMode == JsonRpcInputMode.MultipleDocuments ? SocketJsonReaderOptions : default);

    private static JsonRpcRequest CreateRequest(JsonRpcEnvelope envelope, JsonElement paramsElement, ReadOnlyMemory<byte> paramsUtf8) =>
        new()
        {
            JsonRpc = envelope.JsonRpc!,
            Id = envelope.Id,
            Method = envelope.Method!,
            Params = paramsElement,
            ParamsUtf8 = paramsUtf8,
            ParamsKind = envelope.HasParams ? envelope.ParamsKind : JsonValueKind.Undefined
        };

    private static int CountLeadingJsonWhitespace(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty || !IsJsonWhitespace(span[0]))
        {
            return 0;
        }

        if (span.Length == 1)
        {
            return 1;
        }

        int index = span.IndexOfAnyExcept(JsonWhitespace);
        return index >= 0 ? index : span.Length;
    }

    private static bool HasNonWhitespace(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
        {
            return false;
        }

        if (!IsJsonWhitespace(span[0]))
        {
            return true;
        }

        if (span.Length == 1)
        {
            return false;
        }

        return span.IndexOfAnyExcept(JsonWhitespace) >= 0;
    }

    private static bool IsJsonWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
}
