// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Xdc.Types;

namespace Nethermind.Xdc.Test.Helpers;

internal class TestRandomSigner(List<PrivateKey> masternodeCandidates, IBlockTree blockTree, IEpochSwitchManager epochSwitchManager) : ISigner
{
    private readonly Random _rnd = new();
    private readonly EthereumEcdsa _ecdsa = new(0);
    public PrivateKey? Key { get; private set; }

    public Address Address => Key!.Address;

    public bool CanSign => true;

    public bool TrySign(in ValueHash256 message, [NotNullWhen(true)] out Signature signature)
    {
        EpochSwitchInfo switchInfo = epochSwitchManager.GetEpochSwitchInfo((XdcBlockHeader)blockTree.Head!.Header)!;
        Address c = switchInfo.Masternodes[_rnd.Next(switchInfo.Masternodes.Length)];
        Key = masternodeCandidates.Find(k => k.Address == c)!;
        signature = _ecdsa.Sign(Key, in message);
        return true;
    }

    public bool TrySign(Transaction tx) => throw new NotImplementedException();
}
