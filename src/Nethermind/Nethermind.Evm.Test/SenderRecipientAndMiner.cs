// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;

namespace Nethermind.Evm.Test
{
    public class SenderRecipientAndMiner
    {
        public static SenderRecipientAndMiner Default = new();

        public SenderRecipientAndMiner()
        {
            SenderKey = TestItem.PrivateKeyA;
            RecipientKey = TestItem.PrivateKeyB;
            MinerKey = TestItem.PrivateKeyD;
        }

        public PrivateKey SenderKey { get; set; }

        public PrivateKey RecipientKey { get; set; }

        public PrivateKey MinerKey { get; set; }

        public Address Sender => SenderKey.Address;

        public Address Recipient => RecipientKey.Address;

        public Address Miner => MinerKey.Address;
    }
}
