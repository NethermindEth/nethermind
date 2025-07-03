// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Blockchain.HistoryPruning;

public interface IHistoryPruner
{
    void OnBlockProcessorQueueEmpty(object? sender, EventArgs e);
    Task TryPruneHistory(CancellationToken cancellationToken);
}
