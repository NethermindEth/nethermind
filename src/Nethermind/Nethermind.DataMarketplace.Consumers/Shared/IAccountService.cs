// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.DataMarketplace.Core.Events;

namespace Nethermind.DataMarketplace.Consumers.Shared
{
    public interface IAccountService
    {
        event EventHandler<AddressChangedEventArgs> AddressChanged;
        Address? GetAddress();
        Task ChangeAddressAsync(Address address);
    }
}
