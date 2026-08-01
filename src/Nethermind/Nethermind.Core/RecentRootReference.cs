// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core;

/// <summary>
/// A recent-root reference declared by a frame transaction: <c>[source_id, slot, root]</c>.
/// https://eips.ethereum.org/EIPS/eip-8272
/// </summary>
/// <remarks>The root is opaque to consensus — applications bind its meaning. The slot is a beacon slot number.</remarks>
public class RecentRootReference(in ValueHash256 sourceId, ulong slot, in ValueHash256 root)
{
    public ValueHash256 SourceId { get; } = sourceId;
    public ulong Slot { get; } = slot;
    public ValueHash256 Root { get; } = root;
}
