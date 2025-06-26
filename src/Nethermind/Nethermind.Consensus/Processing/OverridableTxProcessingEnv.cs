// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.State;

namespace Nethermind.Consensus.Processing;

public class OverridableTxProcessingEnv(IOverridableCodeInfoRepository codeInfoRepository, ITransactionProcessor transactionProcessor, IOverridableWorldScope overrideWorldScope, ISpecProvider specProvider) : IOverridableTxProcessorSource
{
    public IOverridableTxProcessingScope Build(Hash256 stateRoot)
    {
        return new OverridableTxProcessingScope(codeInfoRepository, transactionProcessor, overrideWorldScope, stateRoot);
    }

    public IOverridableTxProcessingScope BuildAndOverride(BlockHeader header, Dictionary<Address, AccountOverride>? stateOverride)
    {
        IOverridableTxProcessingScope scope = Build(header.StateRoot ?? throw new ArgumentException($"Block {header.Hash} state root is null", nameof(header)));
        if (stateOverride is not null)
        {
            scope.WorldState.ApplyStateOverrides(scope.CodeInfoRepository, stateOverride, specProvider.GetSpec(header), header.Number);
            header.StateRoot = scope.WorldState.StateRoot;
        }
        return scope;
    }
}
