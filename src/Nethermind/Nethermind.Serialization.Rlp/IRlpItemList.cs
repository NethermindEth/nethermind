// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;

namespace Nethermind.Serialization.Rlp;

public interface IRlpItemList : IDisposable, IRlpWrapper
{
    int Count { get; }
    int RlpLength { get; }
    ReadOnlySpan<byte> ReadContent(int index);
    IRlpItemList CreateNestedItemList(int index);
    RefRlpListReader CreateNestedReader(int index);
}
