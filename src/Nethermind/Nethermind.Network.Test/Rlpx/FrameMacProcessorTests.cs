//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using DotNetty.Common.Utilities;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Rlpx;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class FrameMacProcessorTests
    {
        [Test]
        public void Can_add_and_check_frame_mac()
        {
            byte[] frame = new byte[128];

            FrameMacProcessor macProcessorA = new FrameMacProcessor(TestItem.PublicKeyA, NetTestVectors.GetSecretsPair().A);
            FrameMacProcessor macProcessorB = new FrameMacProcessor(TestItem.PublicKeyA, NetTestVectors.GetSecretsPair().B);
            macProcessorA.AddMac(frame, 0, 112, false);
            macProcessorB.CheckMac(frame, 0, 112, false);
        }

        [Test]
        public void Can_add_and_check_header_mac()
        {
            byte[] header = new byte[32];

            FrameMacProcessor macProcessorA = new FrameMacProcessor(TestItem.PublicKeyA, NetTestVectors.GetSecretsPair().A);
            FrameMacProcessor macProcessorB = new FrameMacProcessor(TestItem.PublicKeyA, NetTestVectors.GetSecretsPair().B);
            macProcessorA.AddMac(header, 0, 16, true);
            macProcessorB.CheckMac(header, 0, 16, true);
        }

        [Test]
        public void Can_add_and_check_both()
        {
            byte[] full = new byte[160];

            FrameMacProcessor macProcessorA = new FrameMacProcessor(TestItem.PublicKeyA, NetTestVectors.GetSecretsPair().A);
            FrameMacProcessor macProcessorB = new FrameMacProcessor(TestItem.PublicKeyA, NetTestVectors.GetSecretsPair().B);
            macProcessorA.AddMac(full, 0, 16, true);
            macProcessorA.AddMac(full, 32, 112, false);
            macProcessorB.CheckMac(full, 0, 16, true);
            macProcessorB.CheckMac(full, 32, 112, false);
        }

        [Test]
        public void Egress_update_chunks_should_not_matter()
        {
            byte[] a1 = new byte[160];
            byte[] b1 = new byte[160];

            byte[] egressUpdate = new byte[32];
            for (int i = 0; i < egressUpdate.Length; i++)
            {
                egressUpdate[i] = (byte) i;
            }

            var secretsA = NetTestVectors.BuildSecretsWithSameIngressAndEgress();
            secretsA.EgressMac.BlockUpdate(egressUpdate.Slice(0, 16), 0, 16);
            secretsA.EgressMac.BlockUpdate(egressUpdate.Slice(16, 16), 0, 16);
            FrameMacProcessor macProcessorA = new FrameMacProcessor(TestItem.PublicKeyA, secretsA);
            macProcessorA.AddMac(a1, 0, 16, false);

            var secretsB = NetTestVectors.BuildSecretsWithSameIngressAndEgress();
            secretsB.EgressMac.BlockUpdate(egressUpdate, 0, 32);
            FrameMacProcessor macProcessorB = new FrameMacProcessor(TestItem.PublicKeyA, secretsB);
            macProcessorB.AddMac(b1, 0, 16, false);

            Assert.AreEqual(a1.Slice(16, 16), b1.Slice(16, 16));
        }
    }
}
