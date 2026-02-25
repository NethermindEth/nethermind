// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Collections;

public interface IByteArrayList : IDisposable
{
    int Count { get; }
    ReadOnlySpan<byte> this[int index] { get; }
}
