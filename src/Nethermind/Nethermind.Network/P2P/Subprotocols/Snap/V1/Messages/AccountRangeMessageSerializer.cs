// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public class AccountRangeMessageSerializer : IOwningMessageSerializer<AccountRangeMessage>
    {
        public int Serialize(Span<byte> destination, AccountRangeMessage message)
        {
            RlpWriter writer = RlpWriter.ContentWriter(destination);
            RlpSequence sequence = writer.StartSequence(GetContentLength(message));

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

            writer.EndSequence(sequence);
            return writer.Length;
        }

        /// <remarks>Copies <paramref name="data"/>, which the message would otherwise keep referencing.</remarks>
        public AccountRangeMessage Deserialize(ReadOnlySpan<byte> data) => Deserialize(OwnedMemory.Copy(data, out Memory<byte> memory), memory);

        public AccountRangeMessage Deserialize(IMemoryOwner<byte> owner, Memory<byte> data)
        {
            IMemoryOwner<byte>? memoryOwner = owner;
            AccountRangeMessage message = new();

            try
            {
                RlpReader outer = new(data.Span);
                RlpReader ctx = outer.EnterSequence(out int itemLength);
                message.RequestId = ctx.DecodeLong();

                int count = ValidatePathsWithAccounts(ctx);
                Memory<byte> pwasRegion = data.SliceOf(ctx.ReadEncodedItem());

                RefCountingMemoryOwner<byte> sharedOwner = new(owner);
                memoryOwner = sharedOwner;
                message.PathsWithAccounts = new RlpPathWithAccountList(new RlpItemList(sharedOwner, pwasRegion, count));
                // The proofs stay in the same memory, so they take a lease of their own on its owner.
                sharedOwner.AcquireLease();
                message.Proofs = RlpByteArrayList.DecodeList(ref ctx, sharedOwner, data, SnapMessageLimits.AccountRangeProofsRlpLimit);
                memoryOwner = null;

                outer.ExitSequence(ctx, itemLength);
                outer.CheckEnd();

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
        /// <param name="ctx">A copy of the message reader, positioned at the entries.</param>
        /// <returns>The number of entries.</returns>
        private static int ValidatePathsWithAccounts(RlpReader ctx)
        {
            RlpReader pwasReader = ctx.EnterSequence(out int pwasLength);
            int count = pwasReader.CountItems(SnapMessageLimits.AccountRangeEntriesRlpLimit);
            for (int i = 0; i < count; i++)
            {
                RlpReader pwaReader = pwasReader.EnterSequence(out int length);
                pwaReader.DecodeValueKeccakNonNull();
                if (!AccountDecoder.Slim.TryDecodeStruct(ref pwaReader, out _))
                    throw new RlpReaderException("An account range entry must contain an account value.");
                pwasReader.ExitSequence(pwaReader, length);
            }

            ctx.ExitSequence(pwasReader, pwasLength);
            return count;
        }

        private static int GetContentLength(AccountRangeMessage message) =>
            Rlp.LengthOf(message.RequestId)
            + (message.PathsWithAccounts?.RlpLength ?? Rlp.LengthOfSequence(0))
            + Rlp.LengthOfByteArrayList(message.Proofs);
    }
}
