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

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Tracing
{
    public class ParityTraceDecoder : IRlpDecoder<ParityLikeTxTrace>
    {
        public ParityLikeTxTrace Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ParityLikeTxTrace trace = new ParityLikeTxTrace();
            rlpStream.ReadSequenceLength();
            trace.BlockHash = rlpStream.DecodeKeccak();
            trace.BlockNumber = (long)rlpStream.DecodeUInt256();
            trace.TransactionHash = rlpStream.DecodeKeccak();
            Span<byte> txPosBytes = rlpStream.DecodeByteArraySpan();
            trace.TransactionPosition = txPosBytes.Length == 0 ? (int?) null : txPosBytes.ReadEthInt32();
            rlpStream.ReadSequenceLength();
            trace.Action = DecodeAction(rlpStream);
            trace.StateChanges = DecodeStateDiff(rlpStream);
            // stateChanges

            return trace;
        }

        private Dictionary<Address, ParityAccountStateChange> DecodeStateDiff(RlpStream rlpStream)
        {
            var accountStateChange = new Dictionary<Address, ParityAccountStateChange>();
            int checkpoint = rlpStream.ReadSequenceLength();
            int items = rlpStream.ReadNumberOfItemsRemaining(rlpStream.Position + checkpoint);
            if (items == 0)
            {
                return null;
            }
            
            for (int i = 0; i < items; i = i + 2)
            {
                accountStateChange[rlpStream.DecodeAddress()] = DecodeAccountStateChange(rlpStream);
            }

            return accountStateChange;
        }

        private ParityAccountStateChange DecodeAccountStateChange(RlpStream rlpStream)
        {
            rlpStream.ReadSequenceLength();
            ParityAccountStateChange change = new ParityAccountStateChange();
            change.Balance = DecodeChange(rlpStream);
            change.Code = DecodeByteChange(rlpStream);
            change.Nonce = DecodeChange(rlpStream);
            change.Storage = DecodeStorageChange(rlpStream);
            return change;
        }

        private Dictionary<UInt256, ParityStateChange<byte[]>> DecodeStorageChange(RlpStream rlpStream)
        {
            int checkpoint = rlpStream.ReadSequenceLength();
            var change = new Dictionary<UInt256, ParityStateChange<byte[]>>();
            int itemsCount = rlpStream.ReadNumberOfItemsRemaining(rlpStream.Position + checkpoint);
            if (itemsCount == 0)
            {
                return null;
            }
            
            for (int i = 0; i < itemsCount; i = i + 2)
            {
                change[rlpStream.DecodeUInt256()] = DecodeByteChange(rlpStream);
            }

            return change;
        }

        private ParityStateChange<UInt256?> DecodeChange(RlpStream rlpStream)
        {
            int sequenceLength = rlpStream.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            ParityStateChange<UInt256?> change = new ParityStateChange<UInt256?>(rlpStream.DecodeNullableUInt256(), rlpStream.DecodeNullableUInt256());
            return change;
        }

        private ParityStateChange<byte[]> DecodeByteChange(RlpStream rlpStream)
        {
            int sequenceLength = rlpStream.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            ParityStateChange<byte[]> change = new ParityStateChange<byte[]>(rlpStream.DecodeByteArray(), rlpStream.DecodeByteArray());
            return change;
        }

        public Rlp Encode(ParityLikeTxTrace item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            Rlp[] traceElements = new Rlp[7];
            traceElements[0] = Rlp.Encode(item.BlockHash);
            traceElements[1] = Rlp.Encode(item.BlockNumber);
            traceElements[2] = Rlp.Encode(item.TransactionHash);
            traceElements[3] = item.TransactionPosition == null ? Rlp.OfEmptyByteArray : Rlp.Encode(item.TransactionPosition.Value);

            ParityTraceAction action = item.Action;
            List<Rlp> calls = new List<Rlp>();
            EncodeAction(calls, action); // trace
            traceElements[4] = Rlp.Encode(calls.ToArray());
            traceElements[5] = EncodeChange(item.StateChanges); // stateDiff
            traceElements[6] = Rlp.OfEmptySequence; // vmTrace placeholder

            return Rlp.Encode(traceElements);
        }

        public void Encode(MemoryStream stream, ParityLikeTxTrace item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public int GetLength(ParityLikeTxTrace item, RlpBehaviors rlpBehaviors)
        {
            throw new NotImplementedException();
        }

        private static ParityTraceAction DecodeAction(RlpStream rlpStream)
        {
            ParityTraceAction action = new ParityTraceAction();
            int sequenceLength = rlpStream.ReadSequenceLength();
            if (rlpStream.ReadNumberOfItemsRemaining(rlpStream.Position + sequenceLength) == 3)
            {
                action.CallType = "reward";
                action.RewardType = rlpStream.DecodeString();
                action.Author = rlpStream.DecodeAddress();
                action.Value = rlpStream.DecodeUInt256();
                action.TraceAddress = Array.Empty<int>();
            }
            else
            {
                action.CallType = rlpStream.DecodeString();
                action.From = rlpStream.DecodeAddress();
                action.To = rlpStream.DecodeAddress();
                action.Value = rlpStream.DecodeUInt256();
                action.Gas = rlpStream.DecodeLong();
                action.Input = rlpStream.DecodeByteArray();
                action.Result = new ParityTraceResult();
                action.Result.Output = rlpStream.DecodeByteArray();
                action.Result.GasUsed = rlpStream.DecodeLong();
                action.TraceAddress = rlpStream.DecodeArray(c => c.DecodeInt());
                int subtracesCount = rlpStream.DecodeInt();
                action.Subtraces = new List<ParityTraceAction>(subtracesCount);
                for (int i = 0; i < subtracesCount; i++)
                {
                    action.Subtraces.Add(DecodeAction(rlpStream));
                }
            }

            return action;
        }

        private static Rlp EncodeChange(ParityStateChange<byte[]> stateChange)
        {
            if (stateChange == null)
            {
                return Rlp.OfEmptySequence;
            }

            return Rlp.Encode(Rlp.Encode(stateChange.Before), Rlp.Encode(stateChange.After));
        }

        private static Rlp EncodeChange(ParityStateChange<UInt256?> stateChange)
        {
            if (stateChange == null)
            {
                return Rlp.OfEmptySequence;
            }

            return Rlp.Encode(Rlp.Encode(stateChange.Before), Rlp.Encode(stateChange.After));
        }

        private static Rlp EncodeChange(ParityAccountStateChange item)
        {
            Rlp[] items = new Rlp[4];
            items[0] = (item.Balance == null ? Rlp.OfEmptySequence : EncodeChange(item.Balance));
            items[1] = (item.Code == null ? Rlp.OfEmptySequence : EncodeChange(item.Code));
            items[2] = (item.Nonce == null ? Rlp.OfEmptySequence : EncodeChange(item.Nonce));
            if (item.Storage == null)
            {
                items[3] = Rlp.OfEmptySequence;
            }
            else
            {
                Rlp[] storageItems = new Rlp[item.Storage.Count * 2];
                int index = 0;
                foreach ((UInt256 address, ParityStateChange<byte[]> change) in item.Storage)
                {
                    storageItems[index++] = (Rlp.Encode(address));
                    storageItems[index++] = (EncodeChange(change));
                }

                items[3] = Rlp.Encode(storageItems);
            }

            return Rlp.Encode(items);
        }

        private static Rlp EncodeChange(Dictionary<Address, ParityAccountStateChange> changes)
        {
            if (changes == null)
            {
                return Rlp.OfEmptySequence;
            }
            
            List<Rlp> items = new List<Rlp>();
            foreach ((Address address, ParityAccountStateChange change) in changes)
            {
                items.Add(Rlp.Encode(address));
                items.Add(EncodeChange(change));
            }

            return Rlp.Encode(items.ToArray());
        }

        private static void EncodeAction(List<Rlp> calls, ParityTraceAction action)
        {
            Rlp[] actionElements;
            if (action.RewardType != null)
            {
                actionElements = new Rlp[3];
                actionElements[0] = Rlp.Encode(action.RewardType);
                actionElements[1] = Rlp.Encode(action.Author);
                actionElements[2] = Rlp.Encode(action.Value);
            }
            else
            {
                actionElements = new Rlp[10];
                actionElements[0] = Rlp.Encode(action.CallType);
                actionElements[1] = Rlp.Encode(action.From);
                actionElements[2] = Rlp.Encode(action.To);
                actionElements[3] = Rlp.Encode(action.Value);
                actionElements[4] = Rlp.Encode(action.Gas);
                actionElements[5] = Rlp.Encode(action.Input);
                actionElements[6] = Rlp.Encode(action.Result?.Output ?? Bytes.Empty);
                actionElements[7] = Rlp.Encode(action.Result?.GasUsed ?? 0L);
                actionElements[8] = Rlp.Encode(action.TraceAddress);
                actionElements[9] = Rlp.Encode(action.Subtraces.Count);
            }

            calls.Add(Rlp.Encode(actionElements));
            foreach (ParityTraceAction subtrace in action.Subtraces)
            {
                EncodeAction(calls, subtrace);
            }
        }
    }
}