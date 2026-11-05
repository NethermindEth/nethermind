// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;

namespace Nethermind.JsonRpc;

/// <summary>
/// A pooled timeout <see cref="CancellationTokenSource"/> scoped to the method that rented it; disposing returns it to the pool.
/// </summary>
internal readonly ref struct RentedTimeout(CancellationTokenSource? source)
{
    public CancellationToken Token => source?.Token ?? CancellationToken.None;

    public void Dispose()
    {
        if (source is not null) JsonRpcConfigExtension.ReturnTimeoutCancellationToken(source);
    }
}
