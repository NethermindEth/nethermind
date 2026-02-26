// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Collections;

public interface IByteArrayList : IDisposable
{
    int Count { get; }
    ReadOnlySpan<byte> this[int index] { get; }
}

public sealed class EmptyByteArrayList : IByteArrayList, IRlpWrapper
{
    public static readonly EmptyByteArrayList Instance = new();
    private static readonly byte[] EmptySequenceRlp = [192]; // 0xC0 = RLP empty list
    public int Count => 0;
    public ReadOnlySpan<byte> this[int index] => throw new IndexOutOfRangeException();
    public ReadOnlySpan<byte> RlpSpan => EmptySequenceRlp;
    public void Dispose() { }
}
