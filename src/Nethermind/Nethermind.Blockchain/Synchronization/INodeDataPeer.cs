// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Synchronization;

public interface INodeDataPeer
{
    Task<IOwnedReadOnlyList<byte[]>> GetNodeData(IReadOnlyList<Hash256> hashes, CancellationToken token);
}
