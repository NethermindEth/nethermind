// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Facade.Proxy.Models.MultiCall;

namespace Nethermind.Facade.Multicall;

public class MultiCallOutput
{
    public string? Error { get; set; }

    public IReadOnlyList<MultiCallBlockResult> Items { get; set; }
}
