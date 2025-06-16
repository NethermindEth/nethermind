// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db;

public interface IMergeOperator
{
    string Name { get; }
    byte[] FullMerge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator, out bool success);
    byte[] PartialMerge(ReadOnlySpan<byte> key, RocksDbMergeEnumerator enumerator, out bool success);
}
