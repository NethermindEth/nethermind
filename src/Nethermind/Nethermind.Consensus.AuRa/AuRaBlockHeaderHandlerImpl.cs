// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa;

/// <summary>
/// Wires the AuRa plugin's <see cref="AuRaBlockHeader"/> subclass into the static
/// <see cref="AuRaBlockHeaderHandler"/> slot so <c>HeaderDecoder</c> (in Nethermind.Serialization.Rlp)
/// and core test builders can materialise the subclass without referencing this plugin.
/// </summary>
internal sealed class AuRaBlockHeaderHandlerImpl : IAuRaBlockHeaderHandler
{
    public static readonly AuRaBlockHeaderHandlerImpl Instance = new();

    // Register at assembly-load time: HeaderDecoder may be invoked before any DI module runs
    // (e.g. RLP-decoding a header on the receive path), so DI-time registration would be too late.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Register() => AuRaBlockHeaderHandler.Register(Instance);
#pragma warning restore CA2255

    public BlockHeader SetSeal(BlockHeader header, long step, byte[]? signature)
    {
        AuRaBlockHeader aura = AuRaBlockHeader.UpgradeFrom(header);
        aura.AuRaStep = step;
        aura.AuRaSignature = signature;
        return aura;
    }
}
