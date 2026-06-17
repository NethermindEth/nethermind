// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;

namespace Nethermind.Serialization.Rlp
{
    public static class ByteArrayExtensions
    {
        public static RlpWriter AsRlpWriter(this byte[]? bytes) =>
            new(bytes);

        public static RlpWriter AsRlpWriter(in this CappedArray<byte> bytes) =>
            new(in bytes);

        public static RlpReader AsRlpContext(this byte[]? bytes) => new(bytes ?? []);

        public static RlpReader AsRlpContext(this Span<byte> span) => ((ReadOnlySpan<byte>)span).AsRlpContext();

        public static RlpReader AsRlpContext(this ReadOnlySpan<byte> span) => span.IsEmpty ? new RlpReader([]) : new RlpReader(span);
    }
}
