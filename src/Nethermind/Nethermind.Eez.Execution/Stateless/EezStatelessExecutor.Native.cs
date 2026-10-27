// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Evm.State;
using Nethermind.Logging;

namespace Nethermind.Eez.Execution.Stateless;

public sealed partial class EezStatelessExecutor
{
    private readonly NativeHistoryContext? _nativeHistory;

    /// <remarks>The caller owns and serializes the L1 evidence for the execution window.</remarks>
    internal EezStatelessExecutor(NativeGenesisSnapshot profile, NativeL1HeaderChain evidence, ILogManager logs)
        : this(profile.Specs, profile.Rules, logs) => _nativeHistory = new NativeHistoryContext(profile, evidence);

    private sealed class NativeHistoryContext(NativeGenesisSnapshot profile, NativeL1HeaderChain evidence)
    {
        private readonly NativeHistorySource _source = new(profile.Rules.Schedule!.L1Reference.ChainId, profile.Specs.ChainId, profile.GenesisHash);

        internal IBlockProcessor.IBlockTransactionsExecutor Wrap(IBlockProcessor.IBlockTransactionsExecutor inner, IWorldState state) =>
            new NativeHistoryBlockExecutor(inner, state, profile.Specs, profile.Rules, profile.History,
                profile.Rules.Schedule!.L1Reference.Number, _source, evidence);
    }
}
