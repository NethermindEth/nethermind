// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Snap;

/// <summary>The accounts of a SNAP account range, read in place from their RLP encoding.</summary>
public interface IPathWithAccountList : IRlpWrapper, IDisposable
{
    int Count { get; }
    RefPathWithAccount this[int index] { get; }
}

public sealed class EmptyPathWithAccountList : IPathWithAccountList
{
    public static readonly EmptyPathWithAccountList Instance = new();
    public int Count => 0;
    public RefPathWithAccount this[int index] => throw new IndexOutOfRangeException();
    public int RlpLength => Rlp.LengthOfSequence(0);
    public void Write(ref RlpWriter writer) => writer.EncodeNullObject();
    public void Dispose() { }
}
