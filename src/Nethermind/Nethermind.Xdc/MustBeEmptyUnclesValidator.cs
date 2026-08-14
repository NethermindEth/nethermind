// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;

namespace Nethermind.Xdc;

public class MustBeEmptyUnclesValidator : IUnclesValidator
{
    public bool Validate(BlockHeader header, BlockHeader[] uncles) => uncles.Length == 0;
}
