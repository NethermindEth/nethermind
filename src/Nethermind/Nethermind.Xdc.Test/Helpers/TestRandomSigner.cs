// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test.Helpers;

internal class TestRandomSigner(List<PrivateKey> masternodeCandidates) : ISigner
{
    private readonly Random _rnd = new Random();
    private EthereumEcdsa _ecdsa = new EthereumEcdsa(0);
    public PrivateKey? Key { get; private set; }

    public Address Address => Key!.Address;

    public bool CanSign => true;

    public Signature Sign(in ValueHash256 message)
    {
        Key = masternodeCandidates[_rnd.Next(masternodeCandidates.Count)];
        return _ecdsa.Sign(Key, in message);
    }

    public ValueTask Sign(Transaction tx)
    {
        throw new NotImplementedException();
    }
}
