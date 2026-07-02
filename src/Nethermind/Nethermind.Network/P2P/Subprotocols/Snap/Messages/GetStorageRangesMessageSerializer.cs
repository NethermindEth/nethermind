// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class GetStorageRangesMessageSerializer : SnapSerializerBase<GetStorageRangeMessage>
    {
        public override void Serialize(IByteBuffer byteBuffer, GetStorageRangeMessage message)
        {
            ByteBufferRlpWriter writer = GetRlpWriterAndStartSequence(byteBuffer, message);

            writer.Encode(message.RequestId);
            writer.Encode(message.StorageRange.RootHash);
            ReadOnlySpan<PathWithAccount> accounts = message.StorageRange.Accounts.AsSpan();
            int accountsCount = accounts.Length;
            int accountsPathsContentLength = accountsCount * Rlp.LengthOfKeccakRlp;
            writer.StartSequence(accountsPathsContentLength);
            for (int i = 0; i < accountsCount; i++)
            {
                writer.Encode(accounts[i].Path);
            }
            writer.Encode(message.StorageRange.StartingHash ?? ValueKeccak.Zero);
            writer.Encode(message.StorageRange.LimitHash ?? ValueKeccak.MaxValue);
            writer.Encode(message.ResponseBytes);
        }

        protected override GetStorageRangeMessage Deserialize(ref RlpReader ctx)
        {
            GetStorageRangeMessage message = new();
            ctx.ReadSequenceLength();

            message.RequestId = ctx.DecodeLong();

            message.StorageRange = new();
            message.StorageRange.RootHash = ctx.DecodeKeccakNonNull();
            message.StorageRange.Accounts = ctx.DecodeArrayPoolList(static (ref RlpReader c) => new PathWithAccount() { Path = c.DecodeKeccakNonNull() }, limit: SnapMessageLimits.GetStorageRangeAccountsRlpLimit);
            message.StorageRange.StartingHash = ctx.DecodeValueKeccakNonNull();
            message.StorageRange.LimitHash = ctx.DecodeValueKeccakNonNull();
            message.ResponseBytes = ctx.DecodeLong();

            return message;
        }

        public override int GetLength(GetStorageRangeMessage message, out int contentLength)
        {
            contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(message.StorageRange.RootHash);
            int accountsCount = message.StorageRange.Accounts.Count;
            int accountsPathsContentLength = accountsCount * Rlp.LengthOfKeccakRlp;
            contentLength += Rlp.LengthOfSequence(accountsPathsContentLength);
            contentLength += Rlp.LengthOf(message.StorageRange.StartingHash ?? ValueKeccak.Zero);
            contentLength += Rlp.LengthOf(message.StorageRange.LimitHash ?? ValueKeccak.MaxValue);
            contentLength += Rlp.LengthOf(message.ResponseBytes);

            return Rlp.LengthOfSequence(contentLength);
        }
    }
}
