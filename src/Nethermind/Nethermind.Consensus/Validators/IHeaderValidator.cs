// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Validators
{
    public interface IHeaderValidator
    {

        bool Validate(BlockHeader header, BlockHeader? parent, bool isUncle, out string? error);
        bool Validate(BlockHeader header, bool isUncle, out string? error);
    }
}
