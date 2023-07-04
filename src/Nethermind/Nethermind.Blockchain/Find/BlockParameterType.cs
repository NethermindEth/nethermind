// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Blockchain.Find
{
    public enum BlockParameterType
    {
        Earliest,
        Finalized,
        Safe,
        Latest,
        Pending,
        BlockNumber,
        BlockHash
    }
}
