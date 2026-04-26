// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Rlp;

public interface IRlpItemList : IDisposable, IRlpWrapper
{
    int Count { get; }
    ReadOnlySpan<byte> ReadContent(int index);
    IRlpItemList GetNestedItemList(int index);
}
