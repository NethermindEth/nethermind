// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.State.StateStore;

public interface IWorldStateProvider : IJournal<Snapshot>, IStateProvider, IStorageProvider
{








    new void Reset();
    new Snapshot TakeSnapshot(bool newTransactionStart = false);
    Snapshot IJournal<Snapshot>.TakeSnapshot() => TakeSnapshot();
}
