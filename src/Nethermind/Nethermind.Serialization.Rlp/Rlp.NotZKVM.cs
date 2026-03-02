// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if !ZKVM
using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp;

public readonly partial struct RlpDecoderKey
{
    public override int GetHashCode() => HashCode.Combine(_type, MemoryMarshal.AsBytes(_key.AsSpan()).FastHash());
}
#endif
