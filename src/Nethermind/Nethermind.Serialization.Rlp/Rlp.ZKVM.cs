// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if ZKVM
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp;

public partial class Rlp
{
    // Under ZKVM/bflat AOT we cannot rely on reflection-based auto-discovery of decoders.
    // The static constructor registers the required decoders explicitly via the builder.
}

public readonly partial struct RlpDecoderKey
{
    public override int GetHashCode() => (int)BitOperations.Crc32C((uint)_type.GetHashCode(), (uint)MemoryMarshal.AsBytes(_key.AsSpan()).FastHash());
}
#endif
