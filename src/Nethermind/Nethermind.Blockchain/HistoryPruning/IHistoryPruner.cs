// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.Blockchain.HistoryPruning;

public interface IHistoryPruner
{
    void TryPruneHistory(CancellationToken cancellationToken);
}
