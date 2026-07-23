// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

namespace Nethermind.State.SnapServer;

/// <summary>Serves contract byte code for snap requests. Independent of state layout.</summary>
public interface ISnapCodeServer
{
    IByteArrayList GetByteCodes(IReadOnlyList<ValueHash256> requestedHashes, long byteLimit, CancellationToken cancellationToken);
}
