// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Validators
{
    public interface IUnclesValidator
    {
        bool Validate(BlockHeader header, BlockHeader[] uncles);
    }
}
