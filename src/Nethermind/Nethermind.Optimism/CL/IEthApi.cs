// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Facade.Eth;

namespace Nethermind.Optimism.CL;

public interface IEthApi
{
    Task<BlockForRpc?> GetBlockByNumber(ulong blockNumber);
}
