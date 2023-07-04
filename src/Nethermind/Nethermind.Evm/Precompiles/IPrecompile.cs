// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.Precompiles
{
    public interface IPrecompile
    {
        Address Address { get; }

        long BaseGasCost(IReleaseSpec releaseSpec);

        long DataGasCost(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec);

        (ReadOnlyMemory<byte>, bool) Run(in ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec);
    }
}
