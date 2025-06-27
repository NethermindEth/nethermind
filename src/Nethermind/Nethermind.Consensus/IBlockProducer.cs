// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus;

public interface IBlockProducer
{
    public const string Factory = "Factory"; // Used in test to denote factory registration instead of the global registration

    Task<Block?> BuildBlock(
        BlockHeader? parentHeader = null,
        IBlockTracer? blockTracer = null,
        PayloadAttributes? payloadAttributes = null,
        Flags flags = Flags.None,
        CancellationToken cancellationToken = default);

    [Flags]
    public enum Flags
    {
        None = 0,
        EmptyBlock = 1,
        DontSeal = 2,

        PrepareEmptyBlock = EmptyBlock | DontSeal,
    }
}
