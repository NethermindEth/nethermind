// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

public partial class CacheCodeInfoRepository
{
    /// <summary>Memo hits served before one is spent on a probe of the code cache.</summary>
    /// <remarks>The guest's per-block <see cref="GuestCodeCache"/> never evicts, so a memo hit has no
    /// eviction ticker to refresh.</remarks>
    private const int MemoHitsPerTickerRefresh = int.MaxValue;
}
