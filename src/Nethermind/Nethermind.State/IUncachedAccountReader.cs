// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;

namespace Nethermind.State;

internal interface IUncachedAccountReader
{
    bool CanReadAccountUncached { get; }

    Account? GetAccountUncached(Address address);
}

internal interface IUncachedStorageTreeProvider
{
    bool CanCreateStorageTreeUncachedAccount { get; }

    IWorldStateScopeProvider.IStorageTree CreateStorageTreeUncachedAccount(Address address);
}
