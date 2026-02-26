// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.Messages
{
    public class AccountRangeMessageSerializer : IZeroMessageSerializer<AccountRangeMessage>
    {
        private readonly AccountDecoder _decoder = new(true);

        public void Serialize(IByteBuffer byteBuffer, AccountRangeMessage message)
        {
            (int contentLength, int pwasLength, int proofsLength) = GetLength(message);

            byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength));

            NettyRlpStream stream = new(byteBuffer);
            stream.StartSequence(contentLength);

            stream.Encode(message.RequestId);
            if (message.PathsWithAccounts is null || message.PathsWithAccounts.Count == 0)
            {
                stream.EncodeNullObject();
            }
            else
            {
                stream.StartSequence(pwasLength);
                for (int i = 0; i < message.PathsWithAccounts.Count; i++)
                {
                    PathWithAccount pwa = message.PathsWithAccounts[i];

                    int accountContentLength = _decoder.GetContentLength(pwa.Account);
                    int pwaLength = Rlp.LengthOf(pwa.Path) + Rlp.LengthOfSequence(accountContentLength);

                    stream.StartSequence(pwaLength);
                    stream.Encode(pwa.Path);
                    _decoder.Encode(pwa.Account, stream, accountContentLength);
                }
            }

            if (!stream.TryWriteRlpWrapper(message.Proofs))
            {
                if (message.Proofs is null || message.Proofs.Count == 0)
                {
                    stream.EncodeNullObject();
                }
                else
                {
                    stream.StartSequence(proofsLength);
                    for (int i = 0; i < message.Proofs.Count; i++)
                    {
                        stream.Encode(message.Proofs[i]);
                    }
                }
            }
        }

        public AccountRangeMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner memoryOwner = new(byteBuffer);
            Rlp.ValueDecoderContext ctx = new(memoryOwner.Memory, true);
            int startPos = ctx.Position;

            ctx.ReadSequenceLength();

            AccountRangeMessage message = new();
            message.RequestId = ctx.DecodeLong();

            int pwasCheck = ctx.ReadSequenceLength() + ctx.Position;
            int count = ctx.PeekNumberOfItemsRemaining(pwasCheck);
            ArrayPoolList<PathWithAccount> pathsWithAccounts = new(count);
            for (int i = 0; i < count; i++)
            {
                ctx.ReadSequenceLength();
                pathsWithAccounts.Add(new PathWithAccount(ctx.DecodeKeccak(), _decoder.Decode(ref ctx)));
            }

            message.PathsWithAccounts = pathsWithAccounts;
            message.Proofs = RlpByteArrayList.DecodeList(ref ctx, memoryOwner);

            byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + (ctx.Position - startPos));

            return message;
        }

        private (int contentLength, int pwasLength, int proofsLength) GetLength(AccountRangeMessage message)
        {
            int contentLength = Rlp.LengthOf(message.RequestId);

            int pwasLength = 0;
            if (message.PathsWithAccounts is null || message.PathsWithAccounts.Count == 0)
            {
                pwasLength = 0;
            }
            else
            {
                for (int i = 0; i < message.PathsWithAccounts.Count; i++)
                {
                    PathWithAccount pwa = message.PathsWithAccounts[i];
                    int itemLength = Rlp.LengthOf(pwa.Path);
                    itemLength += _decoder.GetLength(pwa.Account);

                    pwasLength += Rlp.LengthOfSequence(itemLength);
                }
            }

            contentLength += Rlp.LengthOfSequence(pwasLength);

            int proofsLength = 0;
            if (message.Proofs is IRlpWrapper rlpList)
            {
                contentLength += rlpList.RlpSpan.Length;
            }
            else if (message.Proofs is null || message.Proofs.Count == 0)
            {
                proofsLength = 0;
                contentLength += Rlp.LengthOfSequence(proofsLength);
            }
            else
            {
                for (int i = 0; i < message.Proofs.Count; i++)
                {
                    proofsLength += Rlp.LengthOf(message.Proofs[i]);
                }

                contentLength += Rlp.LengthOfSequence(proofsLength);
            }

            return (contentLength, pwasLength, proofsLength);
        }
    }
}
