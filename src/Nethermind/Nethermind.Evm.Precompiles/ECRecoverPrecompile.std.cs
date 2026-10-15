// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Evm.Precompiles;

public partial class ECRecoverPrecompile
{
    public static ECRecoverPrecompile Instance { get; } = new();
    private static readonly Result<byte[]> Empty = Array.Empty<byte>();

    public static Address Address { get; } = Address.FromNumber(1);

    // The host recovers the SEC1 uncompressed point, prefix included.
    private const int RecoveredPublicKeyLength = 65;

    partial void CountCall() => Metrics.ECRecoverPrecompile++;
}
