// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Common.Utilities;
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

            FrameMacProcessor macProcessorA = new(NetTestVectors.GetSecretsPair().A);
            FrameMacProcessor macProcessorB = new(NetTestVectors.GetSecretsPair().B);
            macProcessorA.AddMac(data.AsSpan(0, dataLength), data.AsSpan(dataLength, Frame.MacSize), isHeader);
            macProcessorB.UpdateIngressMac(data.AsSpan(0, dataLength), isHeader);
            Assert.That(macProcessorB.CheckMac(data.AsSpan(dataLength, Frame.MacSize), isHeader), Is.True);
        }

        [Test]
        public void Can_add_and_check_both()
        {
            byte[] full = new byte[160];

            FrameMacProcessor macProcessorA = new(NetTestVectors.GetSecretsPair().A);
            FrameMacProcessor macProcessorB = new(NetTestVectors.GetSecretsPair().B);
            macProcessorA.AddMac(full.AsSpan(0, 16), full.AsSpan(16, Frame.MacSize), true);
            macProcessorA.AddMac(full.AsSpan(32, 112), full.AsSpan(144, Frame.MacSize), false);
            macProcessorB.UpdateIngressMac(full.AsSpan(0, 16), true);
            Assert.That(macProcessorB.CheckMac(full.AsSpan(16, Frame.MacSize), true), Is.True, "header");
            macProcessorB.UpdateIngressMac(full.AsSpan(32, 112), false);
            Assert.That(macProcessorB.CheckMac(full.AsSpan(144, Frame.MacSize), false), Is.True, "frame");
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
            FrameMacProcessor macProcessorA = new(secretsA);
            macProcessorA.AddMac(a1.AsSpan(0, 16), a1.AsSpan(16, Frame.MacSize), false);

            EncryptionSecrets secretsB = NetTestVectors.BuildSecretsWithSameIngressAndEgress();
            secretsB.EgressMac.Update(egressUpdate);
            FrameMacProcessor macProcessorB = new(secretsB);
            macProcessorB.AddMac(b1.AsSpan(0, 16), b1.AsSpan(16, Frame.MacSize), false);

            Assert.That(b1.Slice(16, 16), Is.EqualTo(a1.Slice(16, 16)));
        }
    }
}
