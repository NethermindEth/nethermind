// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Spec;
using System.Collections.Generic;

namespace Nethermind.Xdc.RPC;

public class XDPoSConfig
{
    public ulong Epoch { get; set; }
    public ulong Gap { get; set; }
    public ulong Period { get; set; }
    public ulong Reward { get; set; }
    public ulong SwitchEpoch { get; set; }
    public ulong SwitchBlock { get; set; }
    public List<V2ConfigParams>? V2Configs { get; set; }
}
