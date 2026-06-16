// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Rlp;

/// <summary>
/// Creates value RLP writers for concrete write targets.
/// </summary>
public static class RlpWriter
{
    /// <summary>
    /// Creates a writer over a caller-owned output span.
    /// </summary>
    public static ValueRlpWriter<IValueRlpWriteBackend.SpanBackend> ForSpan(Span<byte> data) =>
        new(new IValueRlpWriteBackend.SpanBackend(data));

    /// <summary>
    /// Creates a writer over a caller-owned output array.
    /// </summary>
    public static ValueRlpWriter<IValueRlpWriteBackend.SpanBackend> ForArray(byte[]? data) =>
        ForSpan(data ?? []);

    /// <summary>
    /// Creates a writer over a caller-owned capped array.
    /// </summary>
    public static ValueRlpWriter<IValueRlpWriteBackend.SpanBackend> ForCappedArray(in CappedArray<byte> data) =>
        ForSpan((data.IsNotNull ? data : CappedArray<byte>.Empty).AsSpan());

    /// <summary>
    /// Creates a writer over a DotNetty buffer, advancing its writer index as bytes are written.
    /// </summary>
    public static ValueRlpWriter<IValueRlpWriteBackend.ByteBufferBackend> ForByteBuffer(IByteBuffer byteBuffer) =>
        new(new IValueRlpWriteBackend.ByteBufferBackend(byteBuffer));

    /// <summary>
    /// Creates a writer that feeds encoded bytes directly into a Keccak accumulator.
    /// </summary>
    public static ValueRlpWriter<IValueRlpWriteBackend.KeccakBackend> ForKeccak(KeccakHash keccakHash) =>
        new(new IValueRlpWriteBackend.KeccakBackend(keccakHash));

    /// <summary>
    /// Creates a writer over a custom backend. Disposing the writer disposes the backend.
    /// </summary>
    public static ValueRlpWriter<IValueRlpWriteBackend.CustomBackend> ForBackend(IValueRlpWriteBackend backend) =>
        new(new IValueRlpWriteBackend.CustomBackend(backend));

    /// <summary>
    /// Creates a span-backed writer with a new output array of the requested length.
    /// </summary>
    public static ValueRlpWriter<IValueRlpWriteBackend.SpanBackend> ForLength(int length) =>
        ForArray(new byte[length]);
}
