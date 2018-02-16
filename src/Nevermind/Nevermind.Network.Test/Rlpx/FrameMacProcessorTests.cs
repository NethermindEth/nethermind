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

using Nevermind.Network.Rlpx;
using NUnit.Framework;

namespace Nevermind.Network.Test.Rlpx
{
    [TestFixture]
    public class FrameMacProcessorTests
    {
        [Test]
        public void Can_add_and_check_frame_mac()
        {
            byte[] frame = new byte[128];

            FrameMacProcessor macProcessor = new FrameMacProcessor(NetTestVectors.BuildSecretsWithSameIngressAndEgress());
            macProcessor.AddMac(frame, 0, 112, false);
            macProcessor.CheckMac(frame, 0, 112, false);
        }

        [Test]
        public void Can_add_and_check_header_mac()
        {
            byte[] header = new byte[32];

            FrameMacProcessor macProcessor = new FrameMacProcessor(NetTestVectors.BuildSecretsWithSameIngressAndEgress());
            macProcessor.AddMac(header, 0, 16, true);
            macProcessor.CheckMac(header, 0, 16, true);
        }
        
        [Test]
        public void Can_add_and_check_both()
        {
            byte[] full = new byte[160];

            FrameMacProcessor macProcessor = new FrameMacProcessor(NetTestVectors.BuildSecretsWithSameIngressAndEgress());
            macProcessor.AddMac(full, 0, 16, true);
            macProcessor.AddMac(full, 32, 112, false);
            macProcessor.CheckMac(full, 0, 16, true);
            macProcessor.CheckMac(full, 32, 112, false);
        }
    }
}