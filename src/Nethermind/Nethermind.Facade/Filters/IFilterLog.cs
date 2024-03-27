// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Facade.Filters
{
    public interface IFilterLog
    {
        Address Address { get; }
        Hash256 BlockHash { get; }
        long BlockNumber { get; }
        byte[] Data { get; }
        long LogIndex { get; }
        bool Removed { get; }
        Hash256[] Topics { get; }
        Hash256 TransactionHash { get; }
        long TransactionIndex { get; }
        long TransactionLogIndex { get; }
    }
}
