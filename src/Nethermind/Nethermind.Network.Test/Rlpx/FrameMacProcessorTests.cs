// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Common.Utilities;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.Rlpx;
using NUnit.Framework;

namespace Nethermind.Network.Test.Rlpx
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class FrameMacProcessorTests
    {
        [TestCase(128, 112, false, Description = "Frame MAC")]
        [TestCase(32, 16, true, Description = "Header MAC")]
        public void Can_add_and_check_mac(int bufferSize, int dataLength, bool isHeader)
        {
            byte[] data = new byte[bufferSize];

            FrameMacProcessor macProcessorA = new(TestItem.PublicKeyA, NetTestVectors.GetSecretsPair().A);
            FrameMacProcessor macProcessorB = new(TestItem.PublicKeyA, NetTestVectors.GetSecretsPair().B);
            macProcessorA.AddMac(data, 0, dataLength, isHeader);
            macProcessorB.CheckMac(data, 0, dataLength, isHeader);
        }

        [Test]
        public void Can_add_and_check_both()
        {
            byte[] full = new byte[160];

            FrameMacProcessor macProcessorA = new(TestItem.PublicKeyA, NetTestVectors.GetSecretsPair().A);
            FrameMacProcessor macProcessorB = new(TestItem.PublicKeyA, NetTestVectors.GetSecretsPair().B);
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
                egressUpdate[i] = (byte)i;
            }

            EncryptionSecrets secretsA = NetTestVectors.BuildSecretsWithSameIngressAndEgress();
            secretsA.EgressMac.Update(egressUpdate.Slice(0, 16));
            secretsA.EgressMac.Update(egressUpdate.Slice(16, 16));
            FrameMacProcessor macProcessorA = new(TestItem.PublicKeyA, secretsA);
            macProcessorA.AddMac(a1, 0, 16, false);

            EncryptionSecrets secretsB = NetTestVectors.BuildSecretsWithSameIngressAndEgress();
            secretsB.EgressMac.Update(egressUpdate);
            FrameMacProcessor macProcessorB = new(TestItem.PublicKeyA, secretsB);
            macProcessorB.AddMac(b1, 0, 16, false);

            Assert.That(b1.Slice(16, 16), Is.EqualTo(a1.Slice(16, 16)));
        }
    }
}
