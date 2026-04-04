// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

public static class Eip7782Constants
{
    public const ulong TargetBlobCount = 24;  // floor(48/2)
    public const ulong MaxBlobCount = 36;     // floor(72/2)
}
