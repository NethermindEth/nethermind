// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core2.Types;

namespace Nethermind.Core2
{
    public interface IEth1Genesis
    {
        /// <summary>
        /// Eth1 bridge should call this with Eth1 data
        /// </summary>
        /// <returns>true if genesis succeeded; false if the bridge needs to continue gathering deposits</returns>
        Task<bool> TryGenesisAsync(Bytes32 eth1BlockHash, ulong eth1Timestamp);
    }
}
