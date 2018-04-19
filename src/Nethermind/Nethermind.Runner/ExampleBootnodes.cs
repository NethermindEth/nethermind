/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Runner
{
    public class Bootnodes
    {
        public static readonly List<Bootnode> Nethermind;
        public static readonly List<Bootnode> EthJ;
        public static readonly List<Bootnode> MainNetBootnodes;
        public static readonly List<Bootnode> TestNetBootnodes;
        
        static Bootnodes()
        {
            Nethermind = new List<Bootnode>();
            Nethermind.AddRange(
                new[]{
                    new Bootnode("enode://c1a2d0ecc5d76631e6ab7934fc0e420e094b3b02a265872d1e026c70f79ec5ee5d6faf12c20eec05707f0a3ef279a4916b69da93c0f634de4c4299ec1fa6dd08@127.0.0.1:30309", "Nethermind-local"),
                    new Bootnode("enode://e93e38d9069fea998726eb25a5e9bdaadae9161ef8e63508dba807334dced88b53306cc7d6ab931062be7f276594a96cd68e2f874bcbae757178d80bb72ec3e7@10.0.1.4:30309", "Nethermind-vm1-testnet"),
                    new Bootnode("enode://a49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365fdaeb0a70ce47f890cf2f9fca562a7ed784f76eb870a2c75c0f2ab476a70ccb67e92@10.0.1.5:30309", "Nethermind-vm2-testnet"),
                    new Bootnode("enode://65341d5a43f8edfab09d17d014e4ac8fe741bf792a88fbf5e577efef62137c23e5b6d3cf72856dc1896c751643e65d822865d54f5dfa15d1a742e34f9e395f63@10.0.1.5:30303", "ethereumJ-vm2-testnet"),
                });
            
            EthJ = new List<Bootnode>();
            EthJ.Add(
                new Bootnode(
                    "9bcff30ea776ebd28a9424d0ac7aa500d372f918445788f45a807d83186bd52c4c0afaf504d77e2077e5a99f1f264f75f8738646c1ac3673ccc652b65565c3bb",
                    "45.55.204.106",
                    30303,
                    "peer-1.ether.camp"));

            EthJ.Add(
                new Bootnode(
                    "c2b35ed63f5d79c7f160d05c54dd60b3ba32d455dbb10a5fe6fde44854073db02f9a538423a63a480126c74c7f650d77066ae446258e3d00388401d419b99f88",
                    "198.211.114.136",
                    30303,
                    "peer-2.ether.camp"));

            EthJ.Add(
                new Bootnode(
                    "8246787f8d57662b850b354f0b526251eafee1f077fc709460dc8788fa640a597e49ffc727580f3ebbbc5eacb34436a66ea40415fab9d73563481666090a6cf0",
                    "46.101.248.42",
                    30303,
                    "peer-3.ether.camp"));
            
            EthJ.Add(
                new Bootnode("enode://0d837e193233c08d6950913bf69105096457fbe204679d6c6c021c36bb5ad83d167350440670e7fec189d80abc18076f45f44bfe480c85b6c632735463d34e4b@127.0.0.1:30303", "local ethj"));

            TestNetBootnodes = new List<Bootnode>();
            TestNetBootnodes.Add(
                new Bootnode(
                    "30b7ab30a01c124a6cceca36863ece12c4f5fa68e3ba9b0b51407ccc002eeed3b3102d20a88f1c1d3c3154e2449317b8ef95090e77b312d5cc39354f86d5d606",
                    "52.176.7.10",
                    30303,
                    "US-Azure geth"));

            TestNetBootnodes.Add(
                new Bootnode(
                    "865a63255b3bb68023b6bffd5095118fcc13e79dcf014fe4e47e065c350c7cc72af2e53eff895f11ba1bbb6a2b33271c1116ee870f266618eadfc2e78aa7349c",
                    "52.176.100.77",
                    30303,
                    "US-Azure parity"));

            TestNetBootnodes.Add(
                new Bootnode(
                    "6332792c4a00e3e4ee0926ed89e0d27ef985424d97b6a45bf0f23e51f0dcb5e66b875777506458aea7af6f9e4ffb69f43f3778ee73c81ed9d34c51c4b16b0b0f",
                    "52.232.243.152",
                    30303,
                    "Parity"));

            TestNetBootnodes.Add(
                new Bootnode(
                    "94c15d1b9e2fe7ce56e458b9a3b672ef11894ddedd0c6f247e0f1d3487f52b66208fb4aeb8179fce6e3a749ea93ed147c37976d67af557508d199d9594c35f09",
                    "192.81.208.223",
                    30303,
                    "@gpip"));

            TestNetBootnodes.Add(
                new Bootnode(
                    "20c9ad97c081d63397d7b685a412227a40e23c8bdc6688c6f37e97cfbc22d2b4d1db1510d8f61e6a8866ad7f0e17c02b14182d37ea7c3c8b9c2683aeb6b733a1",
                    "52.169.14.227",
                    30303,
                    "sample fast"));
            
            TestNetBootnodes.Add(
                new Bootnode(
                    "343149e4feefa15d882d9fe4ac7d88f885bd05ebb735e547f12e12080a9fa07c8014ca6fd7f373123488102fe5e34111f8509cf0b7de3f5b44339c9f25e87cb8",
                    "52.3.158.184",
                    30303,
                    "from ethj gitter"));

            MainNetBootnodes = new List<Bootnode>();
            MainNetBootnodes.Add(
                new Bootnode(
                    "a979fb575495b8d6db44f750317d0f4622bf4c2aa3365d6af7c284339968eef29b69ad0dce72a4d8db5ebb4968de0e3bec910127f134779fbcb0cb6d3331163c",
                    "52.16.188.185",
                    30303,
                    "Go IE"));

            MainNetBootnodes.Add(
                new Bootnode(
                    "1118980bf48b0a3640bdba04e0fe78b1add18e1cd99bf22d53daac1fd9972ad650df52176e7c7d89d1114cfef2bc23a2959aa54998a46afcf7d91809f0855082",
                    "52.74.57.123",
                    30303,
                    "Go SG"));

            MainNetBootnodes.Add(
                new Bootnode(
                    "78de8a0916848093c73790ead81d1928bec737d565119932b98c6b100d944b7a95e94f847f689fc723399d2e31129d182f7ef3863f2b4c820abbf3ab2722344d",
                    "13.93.211.84",
                    30303,
                    "Go BR"));

            MainNetBootnodes.Add(
                new Bootnode(
                    "3f1d12044546b76342d59d4a05532c14b85aa669704bfe1f864fe079415aa2c02d743e03218e57a33fb94523adb54032871a6c51b2cc5514cb7c7e35b3ed0a99",
                    "13.75.154.138",
                    30303,
                    "Go US West"));

            MainNetBootnodes.Add(
                new Bootnode(
                    "158f8aab45f6d19c6cbf4a089c2670541a8da11978a2f90dbf6a502a4a3bab80d288afdbeb7ec0ef6d92de563767f3b1ea9e8e334ca711e9f8e2df5a0385e8e6",
                    "13.75.154.138",
                    30303,
                    "Go AU"));

            MainNetBootnodes.Add(
                new Bootnode(
                    "979b7fa28feeb35a4741660a16076f1943202cb72b6af70d327f053e248bab9ba81760f39d0701ef1d8f89cc1fbd2cacba0710a12cd5314d5e0c9021aa3637f9",
                    "5.1.83.226",
                    30303,
                    "C++ DE"));
        }
    }
}