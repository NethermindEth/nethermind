// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using Nethermind.Core;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Pbt.Image;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Builds the EIP-8347 anchor from this node's own chain.</summary>
/// <remarks>The artifacts carry no anchor of their own, so an importer and an exporter must describe the
/// anchor the same way for the provenance marker of a seeded database to match the one that seeded it.</remarks>
internal static class PbtMigrationAnchor
{
    /// <summary>Local byte budget for whole code plus its chunk encoding during verification.</summary>
    private const int MaxBufferedCodeBytes = 256 * 1024 * 1024;

    public static PbtImageAnchor Create(ChainSpec chainSpec, BlockHeader genesis, BlockHeader header) =>
        new(chainSpec.ChainId.ToString(CultureInfo.InvariantCulture), genesis.Hash!, header,
            chainSpec.Parameters.Eip8347TransitionTimestamp, MaxBufferedCodeBytes);
}
