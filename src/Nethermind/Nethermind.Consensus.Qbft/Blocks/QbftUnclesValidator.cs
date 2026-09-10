// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;

namespace Nethermind.Consensus.Qbft.Blocks;

/// <summary>BFT blocks never carry ommers.</summary>
public sealed class QbftUnclesValidator : IUnclesValidator
{
    public bool Validate(BlockHeader header, BlockHeader[] uncles) => uncles.Length == 0;
}
