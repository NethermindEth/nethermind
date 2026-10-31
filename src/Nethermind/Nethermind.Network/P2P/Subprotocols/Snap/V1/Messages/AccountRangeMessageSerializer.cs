// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public class AccountRangeMessageSerializer : IZeroMessageSerializer<AccountRangeMessage>
    {
        private readonly AccountDecoder _decoder = new(true);

        public void Serialize(IByteBuffer byteBuffer, AccountRangeMessage message)
        {
            (int contentLength, int pwasLength) = GetLength(message);

            RlpWriter writer = byteBuffer.StartRlpWrite(Rlp.LengthOfSequence(contentLength));
            RlpSequence sequence = writer.StartSequence(contentLength);

            writer.Encode(message.RequestId);
            if (message.PathsWithAccounts is null || message.PathsWithAccounts.Count == 0)
            {
                writer.EncodeNullObject();
            }
            else
            {
                ReadOnlySpan<PathWithAccount> pathsWithAccounts = message.PathsWithAccounts.AsSpan();
                RlpSequence pwas = writer.StartSequence(pwasLength);
                for (int i = 0; i < pathsWithAccounts.Length; i++)
                {
                    PathWithAccount pwa = pathsWithAccounts[i];

                    int accountContentLength = _decoder.GetContentLength(pwa.Account);
                    int pwaLength = Rlp.LengthOf(pwa.Path) + Rlp.LengthOfSequence(accountContentLength);

                    RlpSequence pwaSequence = writer.StartSequence(pwaLength);
                    writer.Encode(pwa.Path);
                    _decoder.Encode(pwa.Account, ref writer, accountContentLength);
                    writer.EndSequence(pwaSequence);
                }

                writer.EndSequence(pwas);
            }

            writer.WriteByteArrayList(message.Proofs);

            writer.EndSequence(sequence);
            byteBuffer.CompleteRlpWrite(in writer, Rlp.LengthOfSequence(contentLength));
        }

        public AccountRangeMessage Deserialize(IByteBuffer byteBuffer)
        {
            NettyBufferMemoryOwner? memoryOwner = new(byteBuffer);
            RlpReader outer = new(memoryOwner.Memory.Span);
            AccountRangeMessage message = new();
            ArrayPoolList<PathWithAccount>? pathsWithAccounts = null;

            try
            {
                RlpReader ctx = outer.EnterSequence(out int itemLength);
                message.RequestId = ctx.DecodeLong();

                RlpReader pwasReader = ctx.EnterSequence(out int pwasLength);
                int count = pwasReader.CountItems(SnapMessageLimits.AccountRangeEntriesRlpLimit);
                pathsWithAccounts = new ArrayPoolList<PathWithAccount>(count);
                for (int i = 0; i < count; i++)
                {
                    RlpReader pwaReader = pwasReader.EnterSequence(out int length);
                    ValueHash256 path = pwaReader.DecodeValueKeccakNonNull();
                    pathsWithAccounts.Add(new PathWithAccount(path, _decoder.DecodeGuardNotNull(ref pwaReader)));
                    pwasReader.ExitSequence(pwaReader, length);
                }

                ctx.ExitSequence(pwasReader, pwasLength);

                message.PathsWithAccounts = pathsWithAccounts;
                pathsWithAccounts = null;
                message.Proofs = RlpByteArrayList.DecodeList(ref ctx, memoryOwner, SnapMessageLimits.AccountRangeProofsRlpLimit);
                memoryOwner = null;

                outer.ExitSequenceIgnoringRest(ctx, itemLength);

                byteBuffer.SetReaderIndex(byteBuffer.ReaderIndex + outer.BytesRead);

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
