// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Evm
{
    public readonly struct ILExecutionContext
    {
        public readonly IBlockhashProvider BlockhashProvider;
        public readonly IWorldState WordState;
    }
}
