// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Modules;

internal interface IExclusiveRpcModulePool
{
    bool SupportsExclusiveRental { get; }
    ValueTask<IRpcModule> RentExclusive(CancellationToken cancellationToken);
}
