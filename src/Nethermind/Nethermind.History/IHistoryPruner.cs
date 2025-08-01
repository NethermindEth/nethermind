// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.History;

public interface IHistoryPruner
{
    public long? CutoffBlockNumber { get; }
    public long? OldestBlockNumber { get; }
}
