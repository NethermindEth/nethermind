// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Mcp.Dto;

public sealed record SyncStatusDto(
    long CurrentBlock,
    long HighestKnownBlock,
    string SyncMode,
    long BlocksBehind,
    int PeerCount);
