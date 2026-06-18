// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Network.Enr;

namespace Nethermind.Network.Discovery;

public interface INodeRecordProvider
{
    ValueTask<NodeRecord> GetCurrentAsync(CancellationToken cancellationToken = default);
}
