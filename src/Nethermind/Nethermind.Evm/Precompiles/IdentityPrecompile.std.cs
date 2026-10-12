// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm.Precompiles;

public sealed partial class IdentityPrecompile
{
    public static Address Address { get; } = Address.FromNumber(4);
}
