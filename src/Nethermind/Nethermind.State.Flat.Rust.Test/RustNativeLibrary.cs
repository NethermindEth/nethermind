// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Rust.Interop;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

internal static class RustNativeLibrary
{
    /// <summary>Ignores the calling test when libnethermind_flatdb.so was not built; see the project's CargoBuildRustFlat target.</summary>
    public static void Require()
    {
        if (!NativeMethods.IsAvailable) Assert.Ignore("libnethermind_flatdb.so not built");
    }
}
