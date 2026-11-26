// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Evm;

namespace Nethermind.Facade.Proxy.Models.Simulate;

[JsonConverter(typeof(BlockStateCallConverterFactory))]
public class BlockStateCall<T>
{
    public BlockOverride? BlockOverrides { get; set; }
    public Dictionary<Address, AccountOverride>? StateOverrides { get; set; }
    public T[]? Calls { get; set; } = [];
}
