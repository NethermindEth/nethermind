// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public sealed class StorageRangesMessageSerializer : IZeroMessageSerializer<StorageRangeMessage>
    {
        private static readonly RlpLimit StorageSlotValueRlpLimit = RlpLimit.For<PathWithStorageSlot>(33, nameof(PathWithStorageSlot.SlotRlpValue));

        public void Serialize(Span<byte> buffer, StorageRangeMessage message)
        {
            (int contentLength, int allSlotsLength, ArrayPoolSpan<int> accountSlotsLengths) = CalculateLengths(message);
            using ArrayPoolSpan<int> returnAccountSlotsLengths = accountSlotsLengths;

            RlpWriter writer = new(buffer);

            writer.StartSequence(contentLength);

            writer.Encode(message.RequestId);

            if (message.Slots is null || message.Slots.Count == 0)
            {
                writer.EncodeNullObject();
            }
            else
            {
                writer.StartSequence(allSlotsLength);
                ReadOnlySpan<IOwnedReadOnlyList<PathWithStorageSlot>> slotsSpan = message.Slots.AsSpan();

                for (int i = 0; i < slotsSpan.Length; i++)
                {
                    writer.StartSequence(accountSlotsLengths[i]);

                    ReadOnlySpan<PathWithStorageSlot> accountSlots = slotsSpan[i].AsSpan();

                    for (int j = 0; j < accountSlots.Length; j++)
                    {
                        PathWithStorageSlot slot = accountSlots[j];

                        int itemLength = Rlp.LengthOf(slot.Path) + Rlp.LengthOf(slot.SlotRlpValue);

                        writer.StartSequence(itemLength);
                        writer.Encode(slot.Path);
                        writer.Encode(slot.SlotRlpValue);
                    }
                }
            }

            writer.WriteByteArrayList(message.Proofs);
        }

        public StorageRangeMessage Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            PooledBuffer? memoryOwner = PooledBuffer.Rent(data.Length);
            data.CopyTo(memoryOwner.Span);
            RlpReader ctx = new(memoryOwner.Memory.Span);

            StorageRangeMessage message = new();

            try
            {
                ctx.ReadSequenceLength();
                message.RequestId = ctx.DecodeLong();

                message.Slots = ctx.DecodeNonNullArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(static (ref RlpReader outerCtx) =>
                    outerCtx.DecodeNonNullArrayPoolList(static (ref RlpReader innerCtx) =>
                    {
                        int length = innerCtx.ReadSequenceLength();
                        int checkPosition = innerCtx.Position + length;
                        ValueHash256 path = innerCtx.DecodeValueKeccakNonNull();
                        byte[] value = innerCtx.DecodeByteArray(StorageSlotValueRlpLimit);
                        innerCtx.Check(checkPosition);
                        return new PathWithStorageSlot(in path, value);
                    }, limit: SnapMessageLimits.StorageRangeSlotsPerAccountRlpLimit),
                    limit: SnapMessageLimits.StorageRangeAccountsRlpLimit,
                    decodeEmptyList: true);
                message.Proofs = RlpByteArrayList.DecodeList(ref ctx, memoryOwner, SnapMessageLimits.StorageRangeProofsRlpLimit);
                memoryOwner = null;

                consumed = ctx.Position;

                return message;
            }
            catch
            {
                message.Dispose();
                memoryOwner?.Dispose();
                throw;
            }
        }

        public int GetLength(StorageRangeMessage message, out int contentLength)
        {
            (int totalContentLength, _, ArrayPoolSpan<int> accountSlotsLengths) = CalculateLengths(message);
            using ArrayPoolSpan<int> returnAccountSlotsLengths = accountSlotsLengths;

            contentLength = totalContentLength;
            return Rlp.LengthOfSequence(totalContentLength);
        }

        private static (int contentLength, int allSlotsLength, ArrayPoolSpan<int> accountSlotsLengths) CalculateLengths(StorageRangeMessage message)
        {
            int contentLength = Rlp.LengthOf(message.RequestId);
            IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>? slots = message.Slots;
            int slotsCount = slots?.Count ?? 0;
            ArrayPoolSpan<int> accountSlotsLengths = new(slotsCount);
            int allSlotsLength = 0;

            try
            {
                if (slots is not null && slotsCount != 0)
                {
                    ReadOnlySpan<IOwnedReadOnlyList<PathWithStorageSlot>> slotsSpan = slots.AsSpan();
                    for (int i = 0; i < slotsSpan.Length; i++)
                    {
                        int accountSlotsLength = 0;

                        ReadOnlySpan<PathWithStorageSlot> accountSlots = slotsSpan[i].AsSpan();
                        for (int j = 0; j < accountSlots.Length; j++)
                        {
                            PathWithStorageSlot slot = accountSlots[j];
                            int slotLength = Rlp.LengthOf(slot.Path) + Rlp.LengthOf(slot.SlotRlpValue);
                            accountSlotsLength += Rlp.LengthOfSequence(slotLength);
                        }

                        accountSlotsLengths[i] = accountSlotsLength;
                        allSlotsLength += Rlp.LengthOfSequence(accountSlotsLength);
                    }
                }

                contentLength += Rlp.LengthOfSequence(allSlotsLength);
                contentLength += Rlp.LengthOfByteArrayList(message.Proofs);

                return (contentLength, allSlotsLength, accountSlotsLengths);
            }
            catch
            {
                accountSlotsLengths.Dispose();
                throw;
            }
        }
    }
}
