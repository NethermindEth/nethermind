// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using System.Collections.Generic;

namespace Nethermind.Xdc;

public class PoolStatus
{
    public IDictionary<(ulong Round, Hash256 Hash), SignerTypes>? Vote { get; set; }
    public IDictionary<(ulong Round, Hash256 Hash), SignerTypes>? Timeout { get; set; }
    public IDictionary<(ulong Round, Hash256 Hash), SyncInfoTypes>? SyncInfo { get; set; }
}
