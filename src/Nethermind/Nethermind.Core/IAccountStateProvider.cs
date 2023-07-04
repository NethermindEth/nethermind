// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    public interface IAccountStateProvider
    {
        Account GetAccount(Address address);
    }
}
