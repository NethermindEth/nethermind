// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.RLP;

internal sealed class SnapshotDecoder : BaseSnapshotDecoder<Snapshot>
{
    protected override Snapshot CreateSnapshot(ulong number, Hash256 hash, Address[] candidates) => new(number, hash, candidates);
}
