// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Collections;

public sealed class ByteArrayListAdapter(IOwnedReadOnlyList<byte[]> inner) : IByteArrayList
{
    public int Count => inner.Count;

    public ReadOnlySpan<byte> this[int index] => inner[index];

    public void Dispose() => inner.Dispose();
}
