// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.State;
using Paprika.Chain;
using IWorldState = Paprika.Chain.IWorldState;

namespace Nethermind.Paprika;

public class PaprikaWorldStateScopeProvider(
    global::Paprika.Chain.Blockchain paprikaBlockchain,
    IKeyValueStoreWithBatching codeDb,
    SemaphoreSlim scopeLock): IWorldStateScopeProvider
{
    public bool HasRoot(BlockHeader? baseBlock)
    {
        return paprikaBlockchain.HasState((baseBlock?.StateRoot ?? Nethermind.Core.Crypto.Keccak.EmptyTreeHash).ToPaprikaKeccak());
    }

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        scopeLock.Wait();

        IWorldState paprikaWorldState = paprikaBlockchain.StartNew(
            (baseBlock?.StateRoot ?? Nethermind.Core.Crypto.Keccak.EmptyTreeHash).ToPaprikaKeccak());

        PaprikaScope scope = new PaprikaScope(paprikaWorldState, new KeyValueWithBatchingBackedCodeDb(codeDb), scopeLock, false, paprikaBlockchain);
        PaprikaScope.DebugBlockNumber = baseBlock?.Number ?? 0;

        if (baseBlock?.Number == 46348)
        {
            PaprikaScope.Debug = true;
        }
        else
        {
            PaprikaScope.Debug = false;
        }

        return scope;
    }
}

public class PaprikaReadOnlyStateScopeProvider(
    global::Paprika.Chain.Blockchain paprikaBlockchain,
    IKeyValueStoreWithBatching codeDb,
#pragma warning disable CS9113 // Parameter is unread.
    SemaphoreSlim scopeLock): IWorldStateScopeProvider
#pragma warning restore CS9113 // Parameter is unread.
{
    public bool HasRoot(BlockHeader? baseBlock)
    {
        return paprikaBlockchain.HasState((baseBlock?.StateRoot ?? Nethermind.Core.Crypto.Keccak.EmptyTreeHash).ToPaprikaKeccak());
    }

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock)
    {
        IReadOnlyWorldState paprikaWorldState = paprikaBlockchain.StartReadOnly(
            (baseBlock?.StateRoot ?? Nethermind.Core.Crypto.Keccak.EmptyTreeHash).ToPaprikaKeccak());

        return new ReadOnlyPaprikaScope(paprikaWorldState, new KeyValueWithBatchingBackedCodeDb(codeDb));
    }
}
