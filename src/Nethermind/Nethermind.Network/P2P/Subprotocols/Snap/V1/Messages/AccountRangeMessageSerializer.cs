// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public class AccountRangeMessageSerializer : IOwningMessageSerializer<AccountRangeMessage>
    {
        public int Serialize(Span<byte> destination, AccountRangeMessage message)
        {
            RlpWriter writer = new(destination);
            writer.StartSequence(GetContentLength(message));

            writer.Encode(message.RequestId);
            if (message.PathsWithAccounts is null)
            {
                writer.EncodeNullObject();
            }
            else
            {
                message.PathsWithAccounts.Write(ref writer);
            }

            writer.WriteByteArrayList(message.Proofs);
            return writer.Position;
        }

        /// <remarks>Copies <paramref name="data"/>, which the message would otherwise keep referencing.</remarks>
        public AccountRangeMessage Deserialize(ReadOnlySpan<byte> data) => Deserialize(OwnedMemory.Copy(data, out Memory<byte> memory), memory);

        public AccountRangeMessage Deserialize(IMemoryOwner<byte> owner, Memory<byte> data)
        {
            IMemoryOwner<byte>? memoryOwner = owner;
            AccountRangeMessage message = new();

            try
            {
                RlpReader ctx = new(data.Span);
                ctx.ReadSequenceLength();
                message.RequestId = ctx.DecodeLong();

                int pwasStart = ctx.Position;
                int count = ValidatePathsWithAccounts(ref ctx);

                RefCountingMemoryOwner<byte> sharedOwner = new(owner);
                memoryOwner = sharedOwner;
                message.PathsWithAccounts = new RlpPathWithAccountList(new RlpItemList(sharedOwner, data[pwasStart..ctx.Position], count));
                // The proofs stay in the same memory, so they take a lease of their own on its owner.
                sharedOwner.AcquireLease();
                message.Proofs = RlpByteArrayList.DecodeList(ref ctx, sharedOwner, data, SnapMessageLimits.AccountRangeProofsRlpLimit);
                memoryOwner = null;
                ctx.CheckEnd();

                return message;
            }
            catch
            {
                message.Dispose();
                memoryOwner?.Dispose();
                throw;
            }
        }

        public int GetLength(AccountRangeMessage message) => Rlp.LengthOfSequence(GetContentLength(message));

        /// <summary>Walks the untrusted entries once, so that reading them from the message later cannot fail.</summary>
        /// <returns>The number of entries.</returns>
        private static int ValidatePathsWithAccounts(ref RlpReader ctx)
        {
            int pwasCheck = ctx.ReadSequenceLength() + ctx.Position;
            int count = ctx.PeekNumberOfItemsRemaining(pwasCheck);
            ctx.GuardLimit(count, SnapMessageLimits.AccountRangeEntriesRlpLimit);
            for (int i = 0; i < count; i++)
            {
                int length = ctx.ReadSequenceLength();
                int checkPosition = ctx.Position + length;
                ctx.DecodeValueKeccakNonNull();
                if (!AccountDecoder.Slim.TryDecodeStruct(ref ctx, out _))
                    throw new RlpException("An account range entry must contain an account value.");
                ctx.Check(checkPosition);
            }

            return count;
        }

        private static int GetContentLength(AccountRangeMessage message) =>
            Rlp.LengthOf(message.RequestId)
            + (message.PathsWithAccounts?.RlpLength ?? Rlp.LengthOfSequence(0))
            + Rlp.LengthOfByteArrayList(message.Proofs);
    }
}
