// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Logging;

namespace Nethermind.Eez.Execution.Settlement.RollupX;

internal sealed class RollupXObservationMachine(IBlockhashProvider hashes, ISpecProvider specs, ILogManager logs)
    : VirtualMachine<EthereumGasPolicy>(hashes, specs, logs), IVirtualMachine
{
    internal static RollupXObservationMachine Create(Witness witness, BlockHeader block, ISpecProvider specs, ILogManager logs)
    {
        if (witness.Headers.Count == 0 || witness.Headers.Count > 256) throw new EezSettlementException("Observation witness has no bounded ancestor chain.");
        using ArrayPoolList<BlockHeader> headers = witness.DecodeHeaders();
        ulong number = block.Number;
        Hash256? parent = block.ParentHash;
        for (int i = headers.Count - 1; i >= 0; i--)
        {
            BlockHeader header = headers[i];
            if (number == 0 || header.Number != --number || header.Hash != parent)
                throw new EezSettlementException("Observation headers differ from the authenticated ancestor chain.");
            parent = header.ParentHash;
        }
        return new(new BlockhashProvider(new StatelessBlockTree(headers), logs), specs, logs);
    }

    protected override bool CanExecutePrecompileCallDirectly(IPrecompile precompile, Address source) =>
        precompile is not RollupXObservationDispatch.CallbackPrecompile && base.CanExecutePrecompileCallDirectly(precompile, source);

    protected override CallResult ExecutePrecompileCall(VmState<EthereumGasPolicy> state, IPrecompile precompile,
        ReadOnlyMemory<byte> data, IReleaseSpec spec)
    {
        if (precompile is not RollupXObservationDispatch.CallbackPrecompile callback)
            return base.ExecutePrecompileCall(state, precompile, data, spec);
        ObservationOutcome outcome = callback.Outcome();
        return new(outcome.Output, precompileSuccess: true, shouldRevert: !outcome.Success,
            exceptionType: outcome.Success ? EvmExceptionType.None : EvmExceptionType.Revert);
    }
}
