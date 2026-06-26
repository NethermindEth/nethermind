// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using System;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class AccountRangeMessageSerializer : IZeroMessageSerializer<AccountRangeMessage>
    {
        private readonly AccountDecoder _decoder = new(true);

        public void Serialize(IByteBuffer byteBuffer, AccountRangeMessage message)
        {
            (int contentLength, int pwasLength) = GetLength(message);

            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));

            ByteBufferRlpWriter writer = new(byteBuffer);
            writer.StartSequence(contentLength);

            writer.Encode(message.RequestId);
            if (message.PathsWithAccounts is null || message.PathsWithAccounts.Count == 0)
            {
                writer.EncodeNullObject();
            }
            else
            {
                ReadOnlySpan<PathWithAccount> pathsWithAccounts = message.PathsWithAccounts.AsSpan();
                writer.StartSequence(pwasLength);
                for (int i = 0; i < pathsWithAccounts.Length; i++)
                {
                    PathWithAccount pwa = pathsWithAccounts[i];

                    int accountLength = _decoder.GetLength(pwa.Account);
                    int pwaLength = Rlp.LengthOf(pwa.Path) + accountLength;

                    writer.StartSequence(pwaLength);
                    writer.Encode(pwa.Path);
                    _decoder.Encode(ref writer, pwa.Account);
                }
            }

            writer.WriteByteArrayList(message.Proofs);
        }

        public AccountRangeMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner? memoryOwner = new(byteBuffer);
            RlpReader ctx = new(memoryOwner.Memory.Span);
            int startPos = ctx.Position;
            AccountRangeMessage message = new();
            ArrayPoolList<PathWithAccount>? pathsWithAccounts = null;

            try
            {
                ctx.ReadSequenceLength();
                message.RequestId = ctx.DecodeLong();

                int pwasCheck = ctx.ReadSequenceLength() + ctx.Position;
                int count = ctx.PeekNumberOfItemsRemaining(pwasCheck);
                ctx.GuardLimit(count, SnapMessageLimits.AccountRangeEntriesRlpLimit);
                pathsWithAccounts = new ArrayPoolList<PathWithAccount>(count);
                for (int i = 0; i < count; i++)
                {
                    int length = ctx.ReadSequenceLength();
                    int checkPosition = ctx.Position + length;
                    ValueHash256 path = ctx.DecodeValueKeccak() ?? throw new RlpException("Unexpected RLP null while decoding account path.");
                    pathsWithAccounts.Add(new PathWithAccount(path, _decoder.Decode(ref ctx)));
                    ctx.Check(checkPosition);
                }

                message.PathsWithAccounts = pathsWithAccounts;
                pathsWithAccounts = null;
                message.Proofs = RlpByteArrayList.DecodeList(ref ctx, memoryOwner, SnapMessageLimits.AccountRangeProofsRlpLimit);
                memoryOwner = null;

                byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startPos));

                return message;
            }
            catch
            {
                pathsWithAccounts?.Dispose();
                message.Dispose();
                memoryOwner?.Dispose();
                throw;
            }
        }

        private (int contentLength, int pwasLength) GetLength(AccountRangeMessage message)
        {
            int contentLength = Rlp.LengthOf(message.RequestId);

            int pwasLength = 0;
            if (message.PathsWithAccounts is not null && message.PathsWithAccounts.Count > 0)
            {
                ReadOnlySpan<PathWithAccount> pathsWithAccounts = message.PathsWithAccounts.AsSpan();
                for (int i = 0; i < pathsWithAccounts.Length; i++)
                {
                    PathWithAccount pwa = pathsWithAccounts[i];
                    int itemLength = Rlp.LengthOf(pwa.Path);
                    itemLength += _decoder.GetLength(pwa.Account);

                    pwasLength += Rlp.LengthOfSequence(itemLength);
                }
            }

            contentLength += Rlp.LengthOfSequence(pwasLength);
            contentLength += Rlp.LengthOfByteArrayList(message.Proofs);

            return (contentLength, pwasLength);
        }
    }
}
