// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Shutter;
public interface IShutterTxSignal
{
    Task WaitForTransactions(ulong slot, CancellationToken cancellationToken);
    bool HaveTransactionsArrived(ulong slot);
}
