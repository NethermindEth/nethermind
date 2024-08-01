// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Blockchain
{
    public static class KnownAddresses
    {
        public static string GetDescription(Address _) => "?";

        public static Dictionary<Address, string> KnownMiners = new()
        {
            { new Address("0x002e08000acbbae2155fab7ac01929564949070d"), "2Miners: SOLO" },
            { new Address("0x005e288d713a5fb3d7c9cf1b43810a98688c7223"), "xnpool" },
            { new Address("0x04668ec2f57cc15c381b461b9fedab5d451c8f7f"), "zhizhu.top" },
            { new Address("0x06b8c5883ec71bc3f4b332081519f23834c8706e"), "Mining Express" },
            { new Address("0x2a5994b501E6A560e727b6C2DE5D856396aaDd38"), "PandaMiner" },
            { new Address("0x2a65aca4d5fc5b5c859090a6c34d164135398226"), "DwarfPool 1" },
            { new Address("0x35f61dfb08ada13eba64bf156b80df3d5b3a738d"), "firepool" },
            { new Address("0x44fd3ab8381cc3d14afa7c4af7fd13cdc65026e1"), "W Pool" },
            { new Address("0x464b0b37db1ee1b5fbe27300acfbf172fd5e4f53"), "FKPool" },
            { new Address("0x4bb96091ee9d802ed039c4d1a5f6216f90f81b01"), "Ethpool 2" },
            { new Address("0x4c549990a7ef3fea8784406c1eecc98bf4211fa5"), "Hiveon Pool" },
            { new Address("0x52bc44d5378309ee2abf1539bf71de1b7d7be3b5"), "Nanopool" },
            { new Address("0x5a0b54d5dc17e0aadc383d2db43b0a0d3e029c4c"), "Spark Pool" },
            { new Address("0x829bd824b016326a401d083b33d092293333a830"), "F2Pool" },
            { new Address("0x9d6d492bD500DA5B33cf95A5d610a73360FcaAa0"), "HuobiMiningPool" },
            { new Address("0xa3c084Ae80a3f03963017669bC696E961d3aE5d5"), "Uleypool" },
            { new Address("0xA7b0536fB02C593b0dfD82bd65aaCBDd19Ae4777"), "Pooling" },
            { new Address("0xb2930b35844a230f00e51431acae96fe543a0347"), "MiningPoolHub" },
            { new Address("0xDA466bF1cE3C69dbeF918817305cF989A6353423"), "MiningPoolHub" },
            { new Address("0xea674fdde714fd979de3edf0f56aa9716b898ec8"), "Ethermine" },
            { new Address("0xeea5b82b61424df8020f5fedd81767f2d0d25bfb"), "BTC.com Pool" }
        };

    }
}
