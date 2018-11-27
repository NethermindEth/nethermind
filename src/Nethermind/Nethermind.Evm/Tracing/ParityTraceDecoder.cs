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
using Nethermind.Core;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Evm.Tracing
{
    public class ParityTraceDecoder : IRlpDecoder<ParityLikeTxTrace>
    {
        private ParityTraceDecoder()
        {
        }

        static ParityTraceDecoder()
        {
            Rlp.Decoders[typeof(ParityLikeTxTrace)] = new ParityTraceDecoder();
        }

        public ParityLikeTxTrace Decode(Rlp.DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            ParityLikeTxTrace trace = new ParityLikeTxTrace();
            context.ReadSequenceLength();
            trace.BlockHash = context.DecodeKeccak();
            trace.BlockNumber = context.DecodeUInt256();
            trace.TransactionHash = context.DecodeKeccak();
            byte[] txPosBytes = context.DecodeByteArray();
            trace.TransactionPosition = txPosBytes.Length == 0 ? (int?) null : txPosBytes.ToInt32();
            context.ReadSequenceLength();
            trace.Action = DecodeAction(context);
            trace.StateChanges = DecodeStateDiff(context);
            // stateChanges

            return trace;
        }

        private Dictionary<Address, ParityAccountStateChange> DecodeStateDiff(Rlp.DecoderContext context)
        {
            var accountStateChange = new Dictionary<Address, ParityAccountStateChange>();
            int checkpoint = context.ReadSequenceLength();
            int items = context.ReadNumberOfItemsRemaining(context.Position + checkpoint);
            if (items == 0)
            {
                return null;
            }
            
            for (int i = 0; i < items; i = i + 2)
            {
                accountStateChange[context.DecodeAddress()] = DecodeAccountStateChange(context);
            }

            return accountStateChange;
        }

        private ParityAccountStateChange DecodeAccountStateChange(Rlp.DecoderContext context)
        {
            context.ReadSequenceLength();
            ParityAccountStateChange change = new ParityAccountStateChange();
            change.Balance = DecodeChange(context);
            change.Code = DecodeByteChange(context);
            change.Nonce = DecodeChange(context);
            change.Storage = DecodeStorageChange(context);
            return change;
        }

        private Dictionary<UInt256, ParityStateChange<byte[]>> DecodeStorageChange(Rlp.DecoderContext context)
        {
            int checkpoint = context.ReadSequenceLength();
            var change = new Dictionary<UInt256, ParityStateChange<byte[]>>();
            int itemsCount = context.ReadNumberOfItemsRemaining(context.Position + checkpoint);
            if (itemsCount == 0)
            {
                return null;
            }
            
            for (int i = 0; i < itemsCount; i = i + 2)
            {
                change[context.DecodeUInt256()] = DecodeByteChange(context);
            }

            return change;
        }

        private ParityStateChange<UInt256> DecodeChange(Rlp.DecoderContext context)
        {
            int sequenceLength = context.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            ParityStateChange<UInt256> change = new ParityStateChange<UInt256>(context.DecodeUInt256(), context.DecodeUInt256());
            return change;
        }

        private ParityStateChange<byte[]> DecodeByteChange(Rlp.DecoderContext context)
        {
            int sequenceLength = context.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            ParityStateChange<byte[]> change = new ParityStateChange<byte[]>(context.DecodeByteArray(), context.DecodeByteArray());
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

        private static ParityTraceAction DecodeAction(Rlp.DecoderContext context)
        {
            ParityTraceAction action = new ParityTraceAction();
            int sequenceLength = context.ReadSequenceLength();
            if (context.ReadNumberOfItemsRemaining(context.Position + sequenceLength) == 3)
            {
                action.CallType = "reward";
                action.RewardType = context.DecodeString();
                action.Author = context.DecodeAddress();
                action.Value = context.DecodeUInt256();
                action.TraceAddress = Array.Empty<int>();
            }
            else
            {
                action.CallType = context.DecodeString();
                action.From = context.DecodeAddress();
                action.To = context.DecodeAddress();
                action.Value = context.DecodeUInt256();
                action.Gas = context.DecodeLong();
                action.Input = context.DecodeByteArray();
                action.Result = new ParityTraceResult();
                action.Result.Output = context.DecodeByteArray();
                action.Result.GasUsed = context.DecodeLong();
                action.TraceAddress = context.DecodeArray(c => c.DecodeInt());
                int subtracesCount = context.DecodeInt();
                action.Subtraces = new List<ParityTraceAction>(subtracesCount);
                for (int i = 0; i < subtracesCount; i++)
                {
                    action.Subtraces.Add(DecodeAction(context));
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

        private static Rlp EncodeChange(ParityStateChange<UInt256> stateChange)
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
                actionElements[6] = Rlp.Encode(action.Result.Output);
                actionElements[7] = Rlp.Encode(action.Result.GasUsed);
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