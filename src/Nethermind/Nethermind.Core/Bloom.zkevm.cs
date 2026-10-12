// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

// Lazy, so the guest runs no class constructor for this type.
public partial class Bloom
{
    public static Bloom ZeroLength => field ??= CreateZeroLength();
}
