// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.State.SnapServer;

/// <summary>Serves block access lists for snap requests (EIP-8189).</summary>
/// <remarks>Implemented outside the state layer since it depends on the block tree and the block-access-list store
/// rather than on how state is stored.</remarks>
public interface ISnapBalServer
{
    IByteArrayList GetBlockAccessLists(IReadOnlyList<ValueHash256> blockHashes, long byteLimit, CancellationToken cancellationToken);
}
