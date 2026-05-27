// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace RpcTestsMon.Notifiers;

internal interface INotifier
{
    Task NotifyMismatchAsync(MismatchInfo info, CancellationToken ct);
    Task NotifyErrorAsync(string message);
}
