// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;

namespace Nethermind.Serialization.Rlp
{
    public static class ByteArrayExtensions
    {
        public static ValueRlpWriter<IValueRlpWriteBackend.SpanBackend> AsRlpValueWriter(this byte[]? bytes) =>
            RlpWriter.ForArray(bytes);

        public static ValueRlpWriter<IValueRlpWriteBackend.SpanBackend> AsRlpValueWriter(in this CappedArray<byte> bytes) =>
            RlpWriter.ForCappedArray(in bytes);

        public static ValueRlpReader AsRlpValueContext(this byte[]? bytes) => new(bytes ?? []);

        public static ValueRlpReader AsRlpValueContext(this Span<byte> span) => ((ReadOnlySpan<byte>)span).AsRlpValueContext();

        public static ValueRlpReader AsRlpValueContext(this ReadOnlySpan<byte> span) => span.IsEmpty ? new ValueRlpReader([]) : new ValueRlpReader(span);
    }
}
