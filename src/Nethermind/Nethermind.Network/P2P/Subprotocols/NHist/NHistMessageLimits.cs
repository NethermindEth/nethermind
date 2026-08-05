// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.P2P.Subprotocols.NHist.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.NHist;

internal static class NHistMessageLimits
{
    public const int MaxCursorBytes = 64;
    public const int MaxResponseEntries = 131_072;
    public const int MaxResponseChunks = 4_096;
    public const int MaxServedScopes = 4_096;
    public const long MaxResponseBytes = 3_145_728;

    public static readonly RlpLimit CursorRlpLimit = RlpLimit.For<GetHistoryRangeAtHeightMessage>(MaxCursorBytes, nameof(GetHistoryRangeAtHeightMessage.Cursor));
    public static readonly RlpLimit HistoryRangeEntriesRlpLimit = RlpLimit.For<HistoryRangeAtHeightMessage>(MaxResponseEntries, nameof(HistoryRangeAtHeightMessage.Entries));
    public static readonly RlpLimit ChangesetChunksRlpLimit = RlpLimit.For<ChangesetsMessage>(MaxResponseChunks, nameof(ChangesetsMessage.Chunks));
    public static readonly RlpLimit ServedScopesRlpLimit = RlpLimit.For<NHistStatusMessage>(MaxServedScopes, nameof(NHistStatusMessage.Scopes));

    public static long ClampResponseBytes(long requestedBytes) => Math.Clamp(requestedBytes, 1L, MaxResponseBytes);
}
