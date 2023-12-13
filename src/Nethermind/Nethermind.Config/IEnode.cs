// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Config
{
    public interface IEnode
    {
        PublicKey PublicKey { get; }
        Address Address { get; }
        IPAddress HostIp { get; }
        int Port { get; }
        string Info { get; }
    }
}
