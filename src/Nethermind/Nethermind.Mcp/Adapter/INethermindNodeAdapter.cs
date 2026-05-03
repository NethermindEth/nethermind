// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Mcp.Dto;

namespace Nethermind.Mcp.Adapter;

public interface INethermindNodeAdapter
{
    NodeVersionDto GetNodeVersion();

    SyncStatusDto GetSyncStatus();

    NodeHealthDto GetNodeHealth();
}
