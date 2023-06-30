// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Logging;

namespace Nethermind.Consensus.Processing;

public interface IMultiCallBlocksProcessingEnv : IReadOnlyTxProcessingEnvBase, IDisposable
{
    //Instance VM can be edited during processing
    ISpecProvider SpecProvider { get; }
    MultiCallVirtualMachine Machine { get; }

    //We need abilety to get many instances that do not conflict in terms of editable tmp storage - thus we implement env cloning
    IMultiCallBlocksProcessingEnv Clone();

    //We keep original ProcessingEnv spirit with Build() that can start from any stateRoot
    IBlockProcessor GetProcessor();
}
