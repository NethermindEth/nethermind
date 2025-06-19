// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;

namespace Nethermind.Db;

public interface IMergeOperator
{
    string Name { get; }
    ArrayPoolList<byte>? FullMerge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator);
    ArrayPoolList<byte>? PartialMerge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator);
}
