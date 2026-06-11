// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// Wires the AuRa plugin's <see cref="AuRaBlockHeader"/> subclass into the static
/// <see cref="AuRaBlockHeaderHandler"/> slot so <c>HeaderDecoder</c> and core test builders
/// can materialise the subclass without referencing this plugin.
/// </summary>
public sealed class AuRaBlockHeaderHandlerImpl : IAuRaBlockHeaderHandler
{
    public static readonly AuRaBlockHeaderHandlerImpl Instance = new();

    /// <summary>Register <see cref="Instance"/> as the active handler. Idempotent.</summary>
    public static void Register() => AuRaBlockHeaderHandler.Register(Instance);

    public BlockHeader SetSeal(BlockHeader header, long step, byte[]? signature)
    {
        AuRaBlockHeader aura = AuRaBlockHeader.UpgradeFrom(header);
        aura.AuRaStep = step;
        aura.AuRaSignature = signature;
        return aura;
    }
}
