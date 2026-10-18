// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Evm.State;

internal interface IAccountPrewarmer
{
    void WarmUpAccounts<TEnumerator>(TEnumerator addresses, CancellationToken cancellationToken = default)
        where TEnumerator : struct, IEnumerator<(Address? Sender, Address? Recipient)>, allows ref struct;
}
