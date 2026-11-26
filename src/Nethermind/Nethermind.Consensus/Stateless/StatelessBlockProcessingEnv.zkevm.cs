// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Consensus.Stateless;

public partial class StatelessBlockProcessingEnv
{
    /// <remarks>
    /// The guest executes on <see cref="GuestWorldState"/>: it checks bytecode access itself and resolves each state
    /// access with far fewer lookups than <see cref="WorldState"/>.
    /// </remarks>
    private static partial IWorldState CreateWorldState(IWorldStateScopeProvider scopeProvider, ILogManager logManager) =>
        new GuestWorldState(scopeProvider);

    /// <remarks>A plain, unbounded map rather than <see cref="StaticCodeCache"/>: see <see cref="GuestCodeCache"/>.</remarks>
    private static partial ICodeCache CreateCodeCache() => new GuestCodeCache(CodeCacheCapacity);

    /// <remarks>On Airbender the tries are libairbender's, when it read these witness nodes itself: see
    /// <see cref="AirbenderTrieScopeProvider"/>.</remarks>
    private static partial IWorldStateScopeProvider CreateScopeProvider(Witness witness, ILogManager logManager)
    {
        IKeyValueStoreWithBatching codeDb = witness.CreateCodeDb();
        if (witness.State is AirbenderWitnessNodes nodes && nodes.TryAttach() && codeDb is IWorldStateScopeProvider.ICodeDb nativeCodeDb)
        {
            if (ReportScopeProvider) IO.PrintLine("SCOPE: native");
            return new AirbenderTrieScopeProvider(nativeCodeDb, UnavailableStateHeaderProvider.Instance, logManager);
        }

        if (ReportScopeProvider) IO.PrintLine("SCOPE: managed");
        // Must not share nodes between lookups: the guest's TrieNode.Unseal mutates written nodes in place.
        return new TrieStoreScopeProvider(WitnessNodeStorage.CreateTrieStore(witness.State), codeDb, UnavailableStateHeaderProvider.Instance, logManager);
    }

    // Enabled only in the test builds of the Airbender guest. Production guests print neither line.
    private static bool ReportScopeProvider => false;
}
