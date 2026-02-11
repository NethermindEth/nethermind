// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Xdc;

public class PoolStatus
{
    public Dictionary<string, SignerTypes>? Vote { get; set; }
    public Dictionary<string, SignerTypes>? Timeout { get; set; }
    public Dictionary<string, SyncInfoTypes>? SyncInfo { get; set; }
}
