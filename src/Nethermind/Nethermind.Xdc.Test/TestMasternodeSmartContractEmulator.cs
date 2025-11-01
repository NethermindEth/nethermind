// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc.Test;
public class TestMasternodeSmartContractEmulator
{
    private int MasternodeCount = 100;

    private PrivateKey[] MasternodesPvKeys;

    private int CurrentLeaderIndex;

    public PrivateKey CurrentLeaderPvKey => MasternodesPvKeys[CurrentLeaderIndex];
    public Address CurrentLeaderAddress => CurrentLeaderPvKey.Address;

    private TestMasternodeSmartContractEmulator(int count = 100)
    {
        var pvKeyBuilder = new PrivateKeyGenerator();

        MasternodeCount = count;
        MasternodesPvKeys = pvKeyBuilder.Generate(count).ToArray();
        CurrentLeaderIndex = 0;
    }

    public void RotateLeader()
    {
        CurrentLeaderIndex = (CurrentLeaderIndex + 1) % MasternodeCount;
    }

    private static TestMasternodeSmartContractEmulator? _instance;

    public static TestMasternodeSmartContractEmulator Instance => _instance ??= new TestMasternodeSmartContractEmulator(100);
}
