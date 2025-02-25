// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Core
{
    public interface ILogEntry
    {
        Address Address { get; }
        Hash256[] Topics { get; }
        byte[] Data { get; }
    }
}
