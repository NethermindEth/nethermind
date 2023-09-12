// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Facade.Proxy.Models.MultiCall;

namespace Nethermind.Facade;

public class MultiCallOutput
{
    public string? Error { get; set; }

    public List<MultiCallBlockResult> Items { get; set; }
}
