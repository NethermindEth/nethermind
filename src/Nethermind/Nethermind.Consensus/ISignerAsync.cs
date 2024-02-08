// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.TxPool;
using System.Threading.Tasks;

namespace Nethermind.Consensus
{
    public interface ISignerAsync : ITxSigner
    {
        Task<Signature> Sign(Hash256 message);
        Address Address { get; }
        bool CanSign { get; }
    }
}
