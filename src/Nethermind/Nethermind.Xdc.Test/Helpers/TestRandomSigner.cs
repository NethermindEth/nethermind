// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test.Helpers;

internal class TestRandomSigner(List<PrivateKey> masternodeCandidates, IBlockTree blockTree, IEpochSwitchManager epochSwitchManager) : ISigner
{
    private readonly Random _rnd = new Random();
    private readonly EthereumEcdsa _ecdsa = new EthereumEcdsa(0);
    public PrivateKey? Key { get; private set; }

    public Address Address => Key!.Address;

    public bool CanSign => true;

    public Signature Sign(in ValueHash256 message)
    {
        var switchInfo = epochSwitchManager.GetEpochSwitchInfo((XdcBlockHeader)blockTree.Head!.Header)!;
        var c = switchInfo.Masternodes[_rnd.Next(switchInfo.Masternodes.Length)];
        Key = masternodeCandidates.Find(k => k.Address == c)!;
        return _ecdsa.Sign(Key, in message);
    }

    public ValueTask Sign(Transaction tx)
    {
        throw new NotImplementedException();
    }
}
