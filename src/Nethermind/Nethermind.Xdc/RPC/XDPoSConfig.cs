// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Xdc.Spec;
using System.Collections.Generic;

namespace Nethermind.Xdc;

public class XDPoSConfig
{
    public int Epoch { get; set; }
    public int Gap { get; set; }
    public int Period { get; set; }
    public int Reward { get; set; }
    public int SwitchEpoch { get; set; }
    public long SwitchBlock { get; set; }
    public List<V2ConfigParams>? V2Configs { get; set; }
}
