// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
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

        public NewPooledTransactionHashesMessage68 Deserialize(ReadOnlySpan<byte> data, out int consumed)
        {
            RlpReader ctx = new(data);
            NewPooledTransactionHashesMessage68 msg = Deserialize(ref ctx);
            consumed = ctx.Position;
            return msg;
        }

        private static NewPooledTransactionHashesMessage68 Deserialize(ref RlpReader ctx)
        {
            ctx.ReadSequenceLength();
            PooledMessage message = PooledMessage.Rent();
            try
            {
                message.TypeList.AddRange(ctx.DecodeByteArraySpan(TypesRlpLimit));
                DecodeList(ref ctx, message.SizeList, static (ref RlpReader c) => c.DecodeInt(), SizesRlpLimit);
                DecodeList(ref ctx, message.HashList, static (ref RlpReader c) => c.DecodeValueKeccakNonNull(), HashesRlpLimit);
                return message;
            }
            catch
            {
                message.Dispose();
                throw;
            }
        }

        private static void DecodeList<T>(ref RlpReader reader, ArrayPoolList<T> destination, DecodeRlpValue<T> decode, RlpLimit limit) where T : struct
        {
            int end = reader.ReadSequenceLength() + reader.Position;
            int count = reader.PeekNumberOfItemsRemaining(end, limit.Limit + 1);
            reader.GuardLimit(count, limit);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    destination.Add(decode(ref reader));
                }
                reader.Check(end);
            }
            catch (Exception exception) when (exception is not RlpException)
            {
                throw new RlpException($"Error decoding array of {typeof(T).Name}.", exception);
            }
        }

        private sealed class PooledMessage : NewPooledTransactionHashesMessage68
        {
            private MessageLists? _lists;
            internal ArrayPoolList<byte> TypeList => _lists!.TypeList;
            internal ArrayPoolList<int> SizeList => _lists!.SizeList;
            internal ArrayPoolList<ValueHash256> HashList => _lists!.HashList;

            private PooledMessage(MessageLists lists) : base(lists.TypeList, lists.SizeList, lists.HashList)
                => _lists = lists;

            internal static PooledMessage Rent() => new(MessageLists.Rent());

            /// <inheritdoc/>
            /// <remarks>MessageLists owns the three base-class lists; base.Dispose would dispose arrays retained for reuse.</remarks>
            public override void Dispose() => Interlocked.Exchange(ref _lists, null)?.Return();
        }

        // Only backing storage is reused: a stale message reference must not dispose a subsequent rental.
        private sealed class MessageLists
        {
            private const int MaxRetainedMessages = 32;
            private const int MaxRetainedCapacity = 128;
            // Like StripedLong, separate active slots by 128 bytes to avoid cross-core false sharing.
            private static readonly int SlotStride = 128 / IntPtr.Size;
            private static readonly MessageLists?[] Pool = new MessageLists[(MaxRetainedMessages + 1) * SlotStride];
            private static ref MessageLists? CurrentSlot => ref Pool[(Environment.CurrentManagedThreadId % MaxRetainedMessages + 1) * SlotStride];
            internal readonly ArrayPoolList<byte> TypeList = new(0);
            internal readonly ArrayPoolList<int> SizeList = new(0);
            internal readonly ArrayPoolList<ValueHash256> HashList = new(0);

            internal static MessageLists Rent() => Interlocked.Exchange(ref CurrentSlot, null) ?? new();

            internal void Return()
            {
                // Keep small announcements warm without retaining burst-sized arrays.
                if (TypeList.Capacity <= MaxRetainedCapacity && SizeList.Capacity <= MaxRetainedCapacity && HashList.Capacity <= MaxRetainedCapacity)
                {
                    TypeList.Clear();
                    SizeList.Clear();
                    HashList.Clear();
                    if (Interlocked.CompareExchange(ref CurrentSlot, this, null) is null)
                        return;
                }
                TypeList.Dispose();
                SizeList.Dispose();
                HashList.Dispose();
            }

        }

        public void Serialize(Span<byte> buffer, NewPooledTransactionHashesMessage68 message)
        {
            int sizesLength = 0;
            foreach (int size in message.Sizes.AsSpan())
            {
                sizesLength += Rlp.LengthOf(size);
            }

            int hashesLength = checked(message.Hashes.Count * Rlp.LengthOfKeccakRlp);

            int totalSize = Rlp.LengthOf(message.Types) + Rlp.LengthOfSequence(sizesLength) + Rlp.LengthOfSequence(hashesLength);

            RlpWriter writer = new(buffer);

            writer.StartSequence(totalSize);
            writer.Encode(message.Types.AsSpan());

            writer.StartSequence(sizesLength);
            foreach (int size in message.Sizes.AsSpan())
            {
                writer.Encode(size);
            }

            writer.StartSequence(hashesLength);
            foreach (ref readonly ValueHash256 hash in message.Hashes.AsSpan())
            {
                writer.Encode(in hash);
            }
        }

        public int GetLength(NewPooledTransactionHashesMessage68 message, out int contentLength)
        {
            int sizesLength = 0;
            foreach (int size in message.Sizes.AsSpan())
            {
                sizesLength += Rlp.LengthOf(size);
            }

            int hashesLength = checked(message.Hashes.Count * Rlp.LengthOfKeccakRlp);

            contentLength = Rlp.LengthOf(message.Types) + Rlp.LengthOfSequence(sizesLength) + Rlp.LengthOfSequence(hashesLength);
            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
