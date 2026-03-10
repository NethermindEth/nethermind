// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Utils;

namespace Nethermind.Serialization.Rlp;

public sealed class RefCountingMemoryOwner<T>(IMemoryOwner<T> inner) : RefCountingDisposable, IMemoryOwner<T>
{
    public Memory<T> Memory => inner.Memory;
    protected override void CleanUp() => inner.Dispose();
}
