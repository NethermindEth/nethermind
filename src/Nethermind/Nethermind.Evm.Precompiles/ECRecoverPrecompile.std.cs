// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Precompiles;

public partial class ECRecoverPrecompile
{
    // The host recovers the SEC1 uncompressed point, prefix included.
    private const int RecoveredPublicKeyLength = 65;

    partial void CountCall() => Metrics.ECRecoverPrecompile++;
}
