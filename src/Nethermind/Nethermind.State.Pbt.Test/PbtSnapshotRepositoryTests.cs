// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtSnapshotRepositoryTests
{
    [Test]
    public void DuplicateBaseSnapshot_IsRejected()
    {
        PbtResourcePool pool = new(new PbtConfig());
        PbtSnapshotRepository repository = new();
        StateId state = new(1, default);
        Assert.That(repository.TryAdd(new PbtSnapshot(StateId.PreGenesis, state, default, new PbtSnapshotContent(), pool, PbtResourcePool.Usage.MainBlockProcessing)), Is.True);
        Assert.That(repository.TryAdd(new PbtSnapshot(StateId.PreGenesis, state, default, new PbtSnapshotContent(), pool, PbtResourcePool.Usage.MainBlockProcessing)), Is.False);
        repository.RemoveStatesUntil(ulong.MaxValue);
    }
}
