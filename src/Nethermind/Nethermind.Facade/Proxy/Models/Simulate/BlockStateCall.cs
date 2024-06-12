// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Facade.Proxy.Models.Simulate;

public class BlockStateCall<T>
{
    public BlockOverride? BlockOverrides { get; set; }
    public Dictionary<Address, AccountOverride>? StateOverrides { get; set; }
    public T[]? Calls { get; set; } = Array.Empty<T>();
}
