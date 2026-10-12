// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm.Precompiles;

// Lazy, so the guest runs no class constructor for this type.
public sealed partial class IdentityPrecompile
{
    public static Address Address => field ??= Address.FromNumber(4);
}
