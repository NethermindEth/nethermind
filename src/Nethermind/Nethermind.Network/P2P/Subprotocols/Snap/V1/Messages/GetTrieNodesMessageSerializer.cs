// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;

namespace Nethermind.Network.P2P.Subprotocols.Snap.V1.Messages
{
    public class GetTrieNodesMessageSerializer : IOwningMessageSerializer<GetTrieNodesMessage>
    {
        public int Serialize(Span<byte> destination, GetTrieNodesMessage message)
        {
            RlpWriter writer = new(destination);

            writer.StartSequence(GetContentLength(message));

            writer.Encode(message.RequestId);
            writer.Encode(message.RootHash);

            if (message.Paths is null || message.Paths.Count == 0)
            {
                writer.EncodeNullObject();
            }
            else if (message.Paths is IRlpWrapper wrapper)
            {
                wrapper.Write(ref writer);
            }
            else
            {
                EncodePaths(ref writer, message.Paths);
            }

            writer.Encode(message.Bytes);
            return writer.Position;
        }

        public int GetLength(GetTrieNodesMessage message) => Rlp.LengthOfSequence(GetContentLength(message));

        private static int GetContentLength(GetTrieNodesMessage message)
        {
            int pathsRlpLen;

            if (message.Paths is null || message.Paths.Count == 0)
            {
                pathsRlpLen = 1;
            }
            else if (message.Paths is IRlpWrapper rlpWrapper)
            {
                pathsRlpLen = rlpWrapper.RlpLength;
            }
            else
            {
                pathsRlpLen = GetPathsRlpLength(message.Paths);
            }

            return Rlp.LengthOf(message.RequestId)
                + Rlp.LengthOf(message.RootHash)
                + pathsRlpLen
                + Rlp.LengthOf(message.Bytes);
        }

        private static int GetPathsRlpLength(IOwnedReadOnlyList<PathGroup> paths)
        {
            int contentLength = 0;
            ReadOnlySpan<PathGroup> pathsSpan = paths.AsSpan();
            for (int i = 0; i < pathsSpan.Length; i++)
            {
                byte[][] group = pathsSpan[i].Group;
                int groupContentLength = 0;
                for (int j = 0; j < group.Length; j++)
                {
                    groupContentLength += Rlp.LengthOf(group[j]);
                }
                contentLength += Rlp.LengthOfSequence(groupContentLength);
            }
            return Rlp.LengthOfSequence(contentLength);
        }

        private static void EncodePaths<TWriter>(ref TWriter writer, IOwnedReadOnlyList<PathGroup> paths)
            where TWriter : struct, IRlpWriteBackend, allows ref struct
        {
            int contentLength = 0;
            ReadOnlySpan<PathGroup> pathsSpan = paths.AsSpan();
            for (int i = 0; i < pathsSpan.Length; i++)
            {
                byte[][] group = pathsSpan[i].Group;
                int groupContentLength = 0;
                for (int j = 0; j < group.Length; j++)
                {
                    groupContentLength += Rlp.LengthOf(group[j]);
                }
                contentLength += Rlp.LengthOfSequence(groupContentLength);
            }

            writer.StartSequence(contentLength);
            for (int i = 0; i < pathsSpan.Length; i++)
            {
                byte[][] group = pathsSpan[i].Group;
                int groupContentLength = 0;
                for (int j = 0; j < group.Length; j++)
                {
                    groupContentLength += Rlp.LengthOf(group[j]);
                }
                writer.StartSequence(groupContentLength);
                for (int j = 0; j < group.Length; j++)
                {
                    writer.Encode(group[j]);
                }
            }
        }

        /// <remarks>Copies <paramref name="data"/>, which the message would otherwise keep referencing.</remarks>
        public GetTrieNodesMessage Deserialize(ReadOnlySpan<byte> data) => Deserialize(OwnedMemory.Copy(data, out Memory<byte> memory), memory);

        public GetTrieNodesMessage Deserialize(IMemoryOwner<byte> owner, Memory<byte> data)
        {
            IMemoryOwner<byte>? memoryOwner = owner;
            GetTrieNodesMessage message = new();
            IRlpItemList? rawPaths = null;

            try
            {
                RlpReader ctx = new(data.Span);
                ctx.ReadSequenceLength();
                message.RequestId = ctx.DecodeLong();
                message.RootHash = ctx.DecodeKeccak();

                rawPaths = RlpItemList.DecodeList(ref ctx, owner, data);
                memoryOwner = null;
                ValidatePathGroups(rawPaths);
                message.Paths = new RlpPathGroupList(rawPaths);
                rawPaths = null;

                message.Bytes = ctx.DecodeLong();
                ctx.CheckEnd();

                return message;
            }
            catch
            {
                rawPaths?.Dispose();
                message.Dispose();
                memoryOwner?.Dispose();
                throw;
            }
        }

        private static void ValidatePathGroups(IRlpItemList paths)
        {
            if (paths.Count > SnapMessageLimits.GetTrieNodesPathGroupsRlpLimit.Limit)
            {
                ThrowPathGroupsLimitExceeded(paths.Count);
            }

            for (int i = 0; i < paths.Count; i++)
            {
                using IRlpItemList group = paths.GetNestedItemList(i);
                if (group.Count > SnapMessageLimits.GetTrieNodesPathsPerGroupRlpLimit.Limit)
                {
                    ThrowPathsPerGroupLimitExceeded(group.Count);
                }
            }
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowPathGroupsLimitExceeded(int count)
            => throw new RlpLimitException($"Too many trie path groups in {nameof(GetTrieNodesMessage)}: {count}, max {SnapMessageLimits.GetTrieNodesPathGroupsRlpLimit.Limit}.");

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowPathsPerGroupLimitExceeded(int count)
            => throw new RlpLimitException($"Too many trie paths in a single {nameof(GetTrieNodesMessage)} group: {count}, max {SnapMessageLimits.GetTrieNodesPathsPerGroupRlpLimit.Limit}.");
    }
}
