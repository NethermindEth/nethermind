// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;

namespace Nethermind.Merge.Plugin.BlockProduction;

public interface IMergeBlockProducer : IBlockProducer
{
    IBlockProducer? PreMergeBlockProducer { get; }
    IBlockProducer PostMergeBlockProducer { get; }
}
