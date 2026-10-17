// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Evm.State;

public interface IAccountPrewarmer
{
    void WarmUpAccounts(ReadOnlySpan<Address> addresses, CancellationToken cancellationToken = default);
}
