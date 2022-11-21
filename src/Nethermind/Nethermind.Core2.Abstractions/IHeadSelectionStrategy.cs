// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core2.Crypto;

namespace Nethermind.Core2
{
    public interface IHeadSelectionStrategy
    {
        Task<Root> GetHeadAsync(IStore store);
    }
}
