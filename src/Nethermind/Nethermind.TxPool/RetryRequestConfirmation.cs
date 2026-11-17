// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.TxPool;

internal sealed class RetryRequestConfirmation
{
    internal readonly Lock Sync = new();
    internal int State;
    internal object? PendingAccount;
    internal long PendingSequence;
    internal long PendingDeadline;
}
