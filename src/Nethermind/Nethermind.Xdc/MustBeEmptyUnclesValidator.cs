// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Validators;
using Nethermind.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
public class MustBeEmptyUnclesValidator : IUnclesValidator
{
    public bool Validate(BlockHeader header, BlockHeader[] uncles)
    {
        return uncles.Length == 0;
    }
}
