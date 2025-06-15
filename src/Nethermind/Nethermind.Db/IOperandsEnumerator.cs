// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db;

public interface IOperandsEnumerator
{
    int Count { get; }
    ReadOnlySpan<byte> Get(int index);
}
