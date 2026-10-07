// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Precompiles;

public partial class ECRecoverPrecompile
{
    // The guest recovers the bare 64-byte key.
    private const int RecoveredPublicKeyLength = 64;
}
