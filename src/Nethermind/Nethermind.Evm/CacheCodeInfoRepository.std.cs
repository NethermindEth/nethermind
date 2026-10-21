// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm;

public partial class CacheCodeInfoRepository
{
    /// <summary>Memo hits served before one is spent refreshing the shared cache's eviction ticker.</summary>
    /// <remarks>A memo hit skips the probe that refreshes the ticker, so without this the hottest code
    /// would age as though untouched and could be evicted out from under its own memo — costing a code-db
    /// re-read and re-analysis on the next miss.</remarks>
    private const int MemoHitsPerTickerRefresh = 64;
}
