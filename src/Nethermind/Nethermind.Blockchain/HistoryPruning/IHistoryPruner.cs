// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Blockchain.HistoryPruning;

public interface IHistoryPruner
{
    Task TryPruneHistory(CancellationToken cancellationToken);
}
