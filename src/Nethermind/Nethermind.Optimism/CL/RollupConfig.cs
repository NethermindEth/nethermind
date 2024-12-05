// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Optimism.CL;

public record RollupConfig
{
    public Address L1SystemConfigAddress = Address.Zero;
}
