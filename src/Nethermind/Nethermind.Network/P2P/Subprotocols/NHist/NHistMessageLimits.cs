// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Network.P2P.Subprotocols.NHist.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.State;

namespace Nethermind.Network.P2P.Subprotocols.NHist;

internal static class NHistMessageLimits
{
    public const int MaxResponseChunks = 4_096;
    public const int MaxServedScopes = 4_096;
    public const long MaxResponseBytes = 3_145_728;
    public const int MaxResponseRowEntries = 131_072;

    public static readonly RlpLimit ChangesetChunksRlpLimit = RlpLimit.For<ChangesetsMessage>(MaxResponseChunks, nameof(ChangesetsMessage.Chunks));
    public static readonly RlpLimit ServedScopesRlpLimit = RlpLimit.For<NHistStatusMessage>(MaxServedScopes, nameof(NHistStatusMessage.Scopes));
    public static readonly RlpLimit StartKeyRlpLimit = RlpLimit.For<GetHistoryRowsMessage>(IHistoryServer.MaxRowKeyBytes, nameof(GetHistoryRowsMessage.StartKey));
    public static readonly RlpLimit EndKeyRlpLimit = RlpLimit.For<GetHistoryRowsMessage>(IHistoryServer.MaxRowKeyBytes, nameof(GetHistoryRowsMessage.EndKey));
    public static readonly RlpLimit RowCursorRlpLimit = RlpLimit.For<GetHistoryRowsMessage>(IHistoryServer.MaxRowKeyBytes, nameof(GetHistoryRowsMessage.Cursor));
    public static readonly RlpLimit NextRowCursorRlpLimit = RlpLimit.For<HistoryRowsMessage>(IHistoryServer.MaxRowKeyBytes, nameof(HistoryRowsMessage.NextCursor));
    public static readonly RlpLimit HistoryRowEntriesRlpLimit = RlpLimit.For<HistoryRowsMessage>(MaxResponseRowEntries, nameof(HistoryRowsMessage.Entries));

    public static long ClampResponseBytes(long requestedBytes) => Math.Clamp(requestedBytes, 1L, MaxResponseBytes);
}
