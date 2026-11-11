// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Zkvm.Abstractions;

public static partial class AirbenderRuntime
{
    [LibraryImport("__Internal")]
    [SuppressGCTransition]
    private static partial void airbender_set_zero_jump_target(nint target);
}
