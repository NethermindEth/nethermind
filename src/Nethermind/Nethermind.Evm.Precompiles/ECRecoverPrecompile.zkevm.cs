// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Evm.Precompiles;

public partial class ECRecoverPrecompile
{
    public static ECRecoverPrecompile Instance => field ??= new();
    private static Result<byte[]> Empty => Array.Empty<byte>();

    public static Address Address => field ??= Address.FromNumber(1);

    // The guest recovers the bare 64-byte key.
    private const int RecoveredPublicKeyLength = 64;
}
