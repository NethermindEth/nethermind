// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using DotNetty.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages
{
    public class NewPooledTransactionHashesMessageSerializer
        : IZeroMessageSerializer<NewPooledTransactionHashesMessage68>
    {
        private static readonly RlpLimit TypesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage68>(NethermindSyncLimits.MaxHashesFetch, nameof(NewPooledTransactionHashesMessage68.Types));
        private static readonly RlpLimit SizesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage68>(NethermindSyncLimits.MaxHashesFetch, nameof(NewPooledTransactionHashesMessage68.Sizes));
        private static readonly RlpLimit HashesRlpLimit = RlpLimit.For<NewPooledTransactionHashesMessage68>(NethermindSyncLimits.MaxHashesFetch, nameof(NewPooledTransactionHashesMessage68.Hashes));

        public NewPooledTransactionHashesMessage68 Deserialize(IByteBuffer byteBuffer) =>
            byteBuffer.DeserializeRlp(Deserialize);

        private static NewPooledTransactionHashesMessage68 Deserialize(ref RlpReader ctx)
        {
            ctx.ReadSequenceLength();
            PooledMessage message = PooledMessage.Rent();
            try
            {
                message.TypeList.AddRange(ctx.DecodeByteArraySpan(TypesRlpLimit));
                DecodeList(ref ctx, message.SizeList, static (ref RlpReader c) => c.DecodeInt(), SizesRlpLimit);
                DecodeList(ref ctx, message.HashList.Values, static (ref RlpReader c) => c.DecodeValueKeccakNonNull(), HashesRlpLimit);
                return message;
            }
            catch
            {
                message.Dispose();
                throw;
            }
        }

        private static void DecodeList<T>(ref RlpReader reader, ArrayPoolList<T> destination, DecodeRlpValue<T> decode, RlpLimit limit)
        {
            int end = reader.ReadSequenceLength() + reader.Position;
            int count = reader.PeekNumberOfItemsRemaining(end, limit.Limit + 1);
            reader.GuardLimit(count, limit);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (reader.PeekByte() == Rlp.EmptyListByte)
                        ThrowNullArrayElement(i);
                    T value = decode(ref reader);
                    if (value is null)
                        ThrowNullArrayElement(i);
                    destination.Add(value);
                }
                reader.Check(end);
            }
            catch (Exception exception) when (exception is not RlpException)
            {
                throw new RlpException($"Error decoding array of {typeof(T).Name}.", exception);
            }
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowNullArrayElement(int index) =>
            throw new RlpException($"Null array element at index {index}.");

        private sealed class PooledMessage : NewPooledTransactionHashesMessage68
        {
            private const int MaxRetainedMessages = 32;
            private const int MaxRetainedCapacity = 128;
            // Like StripedLong, separate active slots by 128 bytes to avoid cross-core false sharing.
            private static readonly int SlotStride = 128 / IntPtr.Size;
            private static readonly PooledMessage?[] Pool = new PooledMessage[(MaxRetainedMessages + 1) * SlotStride];
            private static ref PooledMessage? CurrentSlot => ref Pool[(Environment.CurrentManagedThreadId % MaxRetainedMessages + 1) * SlotStride];
            internal readonly ArrayPoolList<byte> TypeList;
            internal readonly ArrayPoolList<int> SizeList;
            internal readonly AnnouncementHashes HashList;
            private int _returned;

            private PooledMessage() : this(new(0), new(0), new()) { }

            private PooledMessage(ArrayPoolList<byte> types, ArrayPoolList<int> sizes, AnnouncementHashes hashes)
                : base(types, sizes, hashes)
            {
                TypeList = types;
                SizeList = sizes;
                HashList = hashes;
            }

            internal static PooledMessage Rent()
            {
                PooledMessage? message = Interlocked.Exchange(ref CurrentSlot, null);
                if (message is not null)
                {
                    message._returned = 0;
                    return message;
                }
                return new();
            }

            public override void Dispose()
            {
                if (Interlocked.Exchange(ref _returned, 1) != 0) return;

                // Keep small announcements warm without retaining burst-sized arrays or hash references.
                if (TypeList.Capacity <= MaxRetainedCapacity && SizeList.Capacity <= MaxRetainedCapacity && HashList.Capacity <= MaxRetainedCapacity)
                {
                    TypeList.Clear();
                    SizeList.Clear();
                    HashList.Clear();
                    AdaptivePacketType = 0;
                    if (Interlocked.CompareExchange(ref CurrentSlot, this, null) is null)
                        return;
                }
                base.Dispose();
            }

        }

        public void Serialize(IByteBuffer byteBuffer, NewPooledTransactionHashesMessage68 message)
        {
            int sizesLength = 0;
            foreach (int size in message.Sizes.AsSpan())
            {
                sizesLength += Rlp.LengthOf(size);
            }

            int hashesLength = 0;
            foreach (Hash256 hash in message.Hashes.AsSpan())
            {
                hashesLength += Rlp.LengthOf(hash);
            }

            int totalSize = Rlp.LengthOf(message.Types) + Rlp.LengthOfSequence(sizesLength) + Rlp.LengthOfSequence(hashesLength);

            byteBuffer.EnsureWritable(totalSize);

            ByteBufferRlpWriter writer = new(byteBuffer);

            writer.StartSequence(totalSize);
            writer.Encode(message.Types.AsSpan());

            writer.StartSequence(sizesLength);
            foreach (int size in message.Sizes.AsSpan())
            {
                writer.Encode(size);
            }

            writer.StartSequence(hashesLength);
            foreach (Hash256 hash in message.Hashes.AsSpan())
            {
                writer.Encode(hash);
            }
        }
    }
}
