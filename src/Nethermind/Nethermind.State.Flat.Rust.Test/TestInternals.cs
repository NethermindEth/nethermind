// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>Internals of the original flat package that the tests call as its consumers do, which this assembly cannot see.</summary>
internal static class TestInternals
{
    /// <summary>Releases the lease a commit hands over with the retired transient resource.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ReleaseLease")]
    public static extern void ReleaseLease(TransientResource transientResource);

    /// <summary>Clears the storage of <paramref name="address"/> in <paramref name="bundle"/>, as a self-destruct does.</summary>
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ClearStorage")]
    public static extern void ClearStorage(SnapshotBundle bundle, Address address, Hash256 addressHash);
}
