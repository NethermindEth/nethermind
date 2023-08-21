// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core
{
    public interface IAccountStateProvider
    {
        Account GetAccount(Address address);
    }
}
