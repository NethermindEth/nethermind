// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using System;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Snap.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public class GetStorageRangesMessageSerializer : SnapSerializerBase<GetStorageRangeMessage>
    {
        public override void Serialize(IByteBuffer byteBuffer, GetStorageRangeMessage message, ref LengthStack lengths)
        {
            ByteBufferRlpWriter writer = GetRlpWriterAndStartSequence(byteBuffer, ref lengths);

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
            writer.Encode(message.StorageRange.StartingHash);
            writer.Encode(message.StorageRange.LimitHash);
            writer.Encode(message.ResponseBytes);
        }

        protected override GetStorageRangeMessage Deserialize(ref RlpReader ctx)
        {
            GetStorageRangeMessage message = new();
            ctx.ReadSequenceLength();

            message.RequestId = ctx.DecodeLong();

            message.StorageRange = new();
            message.StorageRange.RootHash = ctx.DecodeKeccak();
            message.StorageRange.Accounts = ctx.DecodeNonNullArrayPoolList(static (ref RlpReader c) => new PathWithAccount() { Path = c.DecodeKeccak() }, limit: SnapMessageLimits.GetStorageRangeAccountsRlpLimit);
            message.StorageRange.StartingHash = ctx.DecodeValueKeccak();
            message.StorageRange.LimitHash = ctx.DecodeValueKeccak();
            message.ResponseBytes = ctx.DecodeLong();

            return message;
        }

        public override void PopulateLength(GetStorageRangeMessage message, ref LengthStack lengths)
        {
            Hash256 rootHash = message.StorageRange.RootHash
                ?? throw new InvalidOperationException("A storage range request requires a root hash.");
            int contentLength = Rlp.LengthOf(message.RequestId);
            contentLength += Rlp.LengthOf(rootHash);
            int accountsCount = message.StorageRange.Accounts.Count;
            int accountsPathsContentLength = accountsCount * Rlp.LengthOfKeccakRlp;
            contentLength += Rlp.LengthOfSequence(accountsPathsContentLength);
            contentLength += Rlp.LengthOf(message.StorageRange.StartingHash);
            contentLength += Rlp.LengthOf(message.StorageRange.LimitHash);
            contentLength += Rlp.LengthOf(message.ResponseBytes);

            lengths.Push(contentLength);
            lengths.Push(Rlp.LengthOfSequence(contentLength));
        }
    }
}
