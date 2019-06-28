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
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using NUnit.Framework;

namespace Nethermind.Evm.Test.Tracing
{
    [TestFixture]
    class ParityTraceDecoderTests
    {
        [SetUp]
        public void Setup()
        {
            Rlp.RegisterDecoders(typeof(ParityTraceDecoder).Assembly);
        }
        
        [Test]
        public void Can_encode_decode_sample1()
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ParityTraceDecoder).TypeHandle);

            ParityTraceAction subtrace = new ParityTraceAction();
            subtrace.Value = 67890;
            subtrace.CallType = "call";
            subtrace.From = TestItem.AddressC;
            subtrace.To = TestItem.AddressD;
            subtrace.Input = Bytes.Empty;
            subtrace.Gas = 10000;
            subtrace.TraceAddress = new int[] {0, 0};
            subtrace.Result.Output = Bytes.Empty;
            subtrace.Result.GasUsed = 15000;

            ParityLikeTxTrace txTrace = new ParityLikeTxTrace();
            txTrace.Action = new ParityTraceAction();
            txTrace.Action.Value = 12345;
            txTrace.Action.CallType = "init";
            txTrace.Action.From = TestItem.AddressA;
            txTrace.Action.To = TestItem.AddressB;
            txTrace.Action.Input = new byte[] {1, 2, 3, 4, 5, 6};
            txTrace.Action.Gas = 40000;
            txTrace.Action.TraceAddress = new int[] {0};
            txTrace.Action.Subtraces.Add(subtrace);
            txTrace.Action.Result.Output = Bytes.Empty;
            txTrace.Action.Result.GasUsed = 30000;

            txTrace.BlockHash = TestItem.KeccakB;
            txTrace.BlockNumber = 123456;
            txTrace.TransactionHash = TestItem.KeccakC;
            txTrace.TransactionPosition = 5;
            txTrace.Action.TraceAddress = new int[] {1, 2, 3};

            ParityAccountStateChange stateChange = new ParityAccountStateChange();
            stateChange.Balance = new ParityStateChange<UInt256?>(null, 2);
            stateChange.Nonce = new ParityStateChange<UInt256?>(0, 1);
            stateChange.Storage = new Dictionary<UInt256, ParityStateChange<byte[]>>();
            stateChange.Storage[1] = new ParityStateChange<byte[]>(new byte[] {1}, new byte[] {2});

            txTrace.StateChanges = new Dictionary<Address, ParityAccountStateChange>();
            txTrace.StateChanges.Add(TestItem.AddressC, stateChange);

            Rlp rlp = Rlp.Encode(txTrace);
            ParityLikeTxTrace deserialized = Rlp.Decode<ParityLikeTxTrace>(rlp);

            deserialized.Should().BeEquivalentTo(txTrace);
        }

        [Test]
        public void Can_encode_decode_sample2()
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ParityTraceDecoder).TypeHandle);

            ParityTraceAction reward = new ParityTraceAction();
            reward.CallType = "reward";
            reward.Author = TestItem.AddressA;
            reward.RewardType = "block";
            reward.Value = 2.Ether();
            reward.TraceAddress = new int[] { };

            ParityLikeTxTrace txTrace = new ParityLikeTxTrace();
            txTrace.Action = reward;

            txTrace.BlockHash = TestItem.KeccakB;
            txTrace.BlockNumber = 123456;
            txTrace.TransactionHash = null;
            txTrace.TransactionPosition = null;

            ParityAccountStateChange stateChange = new ParityAccountStateChange();
            stateChange.Balance = new ParityStateChange<UInt256?>(0, 2.Ether());

            txTrace.StateChanges = new Dictionary<Address, ParityAccountStateChange>();
            txTrace.StateChanges.Add(TestItem.AddressA, stateChange);

            Rlp rlp = Rlp.Encode(txTrace);
            ParityLikeTxTrace deserialized = Rlp.Decode<ParityLikeTxTrace>(rlp);

            deserialized.Should().BeEquivalentTo(txTrace);
        }

        [Test]
        public void Can_encode_decode_sample3()
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(ParityTraceDecoder).TypeHandle);

            ParityTraceAction subtrace001 = new ParityTraceAction();
            subtrace001.Value = 67890;
            subtrace001.CallType = "call";
            subtrace001.From = TestItem.AddressC;
            subtrace001.To = TestItem.AddressD;
            subtrace001.Input = Bytes.Empty;
            subtrace001.Gas = 10000;
            subtrace001.TraceAddress = new int[] {0, 0, 1};
            subtrace001.Result.Output = Bytes.Empty;
            subtrace001.Result.GasUsed = 15000;

            ParityTraceAction subtrace000 = new ParityTraceAction();
            subtrace000.Value = 67890;
            subtrace000.CallType = "create";
            subtrace000.From = TestItem.AddressC;
            subtrace000.To = TestItem.AddressD;
            subtrace000.Input = Bytes.Empty;
            subtrace000.Gas = 10000;
            subtrace000.TraceAddress = new int[] {0, 0, 2};
            subtrace000.Result.Output = Bytes.Empty;
            subtrace000.Result.GasUsed = 15000;

            ParityTraceAction subtrace00 = new ParityTraceAction();
            subtrace00.Value = 67890;
            subtrace00.CallType = "call";
            subtrace00.From = TestItem.AddressC;
            subtrace00.To = TestItem.AddressD;
            subtrace00.Input = Bytes.Empty;
            subtrace00.Gas = 10000;
            subtrace00.TraceAddress = new int[] {0, 0};
            subtrace00.Result.Output = Bytes.Empty;
            subtrace00.Result.GasUsed = 15000;
            subtrace00.Subtraces.Add(subtrace000);
            subtrace00.Subtraces.Add(subtrace001);

            ParityTraceAction subtrace01 = new ParityTraceAction();
            subtrace01.Value = 67890;
            subtrace01.CallType = "call";
            subtrace01.From = TestItem.AddressC;
            subtrace01.To = TestItem.AddressD;
            subtrace01.Input = Bytes.Empty;
            subtrace01.Gas = 10000;
            subtrace01.TraceAddress = new int[] {0, 1};
            subtrace01.Result.Output = Bytes.Empty;
            subtrace01.Result.GasUsed = 15000;

            ParityLikeTxTrace txTrace = new ParityLikeTxTrace();
            txTrace.Action = new ParityTraceAction();
            txTrace.Action.Value = 12345;
            txTrace.Action.CallType = "init";
            txTrace.Action.From = TestItem.AddressA;
            txTrace.Action.To = TestItem.AddressB;
            txTrace.Action.Input = new byte[] {1, 2, 3, 4, 5, 6};
            txTrace.Action.Gas = 40000;
            txTrace.Action.TraceAddress = new int[] {0};
            txTrace.Action.Subtraces.Add(subtrace00);
            txTrace.Action.Subtraces.Add(subtrace01);
            txTrace.Action.Result.Output = Bytes.Empty;
            txTrace.Action.Result.GasUsed = 30000;

            txTrace.BlockHash = TestItem.KeccakB;
            txTrace.BlockNumber = 123456;
            txTrace.TransactionHash = TestItem.KeccakC;
            txTrace.TransactionPosition = 5;
            txTrace.Action.TraceAddress = new int[] {1, 2, 3};

            Rlp rlp = Rlp.Encode(txTrace);
            ParityLikeTxTrace deserialized = Rlp.Decode<ParityLikeTxTrace>(rlp);

            deserialized.Should().BeEquivalentTo(txTrace);
        }
    }
}