// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Verkle.Utils;

namespace Nethermind.Trie;

public static unsafe class VerkleUtils
{
    public const int VersionLeafKey = 0;
    public const int BalanceLeafKey = 1;
    public const int NonceLeafKey = 2;
    public const int CodeKeccakLeafKey = 3;
    public const int CodeSizeLeafKey = 4;

    const int MainStorageOffsetExponent = 31;

    static readonly UInt256 HeaderStorageOffset = 64;
    static readonly UInt256 CodeOffset = 128;
    static readonly UInt256 VerkleNodeWidth = 256;

    static readonly UInt256 MainStorageOffsetBase = 256;
    static readonly UInt256 MainStorageOffset = MainStorageOffsetBase << MainStorageOffsetExponent;

    public static byte[] GetTreeKeyPrefix(Address address, UInt256 treeIndex)
    {
        // allocate the array on stack
        Span<byte> keyPrefix = stackalloc byte[32];
        // first 12 bytes are '0' padding to convert 12 byte address -> 32 bytes
        Span<byte> cursor = keyPrefix.Slice(12);
        // copy the address to the remaining 20 bytes -
        address.Bytes.CopyTo(cursor);
        byte[] prefix = PedersenHash.Hash(keyPrefix.ToArray(), treeIndex);
        prefix[31] = 0;
        return prefix;
    }

    public static byte[] GetTreeKeyPrefixAccount(Address address) => GetTreeKeyPrefix(address, 0);

    public static byte[] GetTreeKey(Address address, UInt256 treeIndex, byte subIndexBytes)
    {
        byte[] treeKeyPrefix = GetTreeKeyPrefix(address, treeIndex);
        treeKeyPrefix[31] = subIndexBytes;
        return treeKeyPrefix;
    }

    public static byte[] GetTreeKeyForVersion(Address address) => GetTreeKey(address, UInt256.Zero, VersionLeafKey);
    public static byte[] GetTreeKeyForBalance(Address address) => GetTreeKey(address, UInt256.Zero, BalanceLeafKey);
    public static byte[] GetTreeKeyForNonce(Address address) => GetTreeKey(address, UInt256.Zero, NonceLeafKey);
    public static byte[] GetTreeKeyForCodeKeccak(Address address) => GetTreeKey(address, UInt256.Zero, CodeKeccakLeafKey);
    public static byte[] GetTreeKeyForCodeSize(Address address) => GetTreeKey(address, UInt256.Zero, CodeSizeLeafKey);

    public static byte[] GetTreeKeyForCodeChunk(Address address, UInt256 chunk)
    {
        UInt256 chunkOffset = CodeOffset + chunk;

        UInt256 treeIndex = chunkOffset / VerkleNodeWidth;

        UInt256.Mod(chunkOffset, VerkleNodeWidth, out UInt256 subIndex);
        return GetTreeKey(address, treeIndex, subIndex.ToBigEndian()[31]);
    }

    public static byte[] GetTreeKeyForStorageSlot(Address address, UInt256 storageKey)
    {
        UInt256 pos;

        if (storageKey < CodeOffset - HeaderStorageOffset)
        {
            pos = HeaderStorageOffset + storageKey;
        }
        else
        {
            pos = MainStorageOffset + storageKey;
        }

        UInt256 treeIndex = pos / VerkleNodeWidth;

        UInt256.Mod(pos, VerkleNodeWidth, out UInt256 subIndex);
        return GetTreeKey(address, treeIndex, subIndex.ToBigEndian()[31]);
    }


    public static void FillTreeAndSubIndexForChunk(UInt256 chunkId, ref Span<byte> subIndexBytes, out UInt256 treeIndex)
    {
        UInt256 chunkOffset = CodeOffset + chunkId;
        treeIndex = chunkOffset / VerkleNodeWidth;
        UInt256.Mod(chunkOffset, VerkleNodeWidth, out UInt256 subIndex);
        subIndex.ToBigEndian(subIndexBytes);
    }

    public ref struct CodeChunkEnumerator
    {
        const byte PushOffset = 95;
        const byte Push1 = PushOffset + 1;
        const byte Push32 = PushOffset + 32;

        private Span<byte> _code;
        private byte _rollingOverPushLength = 0;
        private readonly byte[] _bufferChunk = new byte[32];
        private readonly Span<byte> _bufferChunkCodePart;

        public CodeChunkEnumerator(Span<byte> code)
        {
            _code = code;
            _bufferChunkCodePart = _bufferChunk.AsSpan().Slice(1);
        }

        // Try get next chunk
        public bool TryGetNextChunk(out byte[] chunk)
        {
            chunk = _bufferChunk;

            // we don't have chunks left
            if (_code.IsEmpty)
            {
                return false;
            }

            // we don't have full chunk
            if (_code.Length < 31)
            {
                // need to have trailing zeroes
                _bufferChunkCodePart.Fill(0);

                // set number of push bytes
                _bufferChunk[0] = _rollingOverPushLength;

                // copy main bytes
                _code.CopyTo(_bufferChunkCodePart);

                // we are done
                _code = Span<byte>.Empty;
            }
            else
            {
                // fill up chunk to store

                // get current chunk of code
                Span<byte> currentChunk = _code.Slice(0, 31);

                // copy main bytes
                currentChunk.CopyTo(_bufferChunkCodePart);

                switch (_rollingOverPushLength)
                {
                    case 32 or 31: // all bytes are roll over

                        // set number of push bytes
                        _bufferChunk[0] = 31;

                        // if 32, then we will roll over with 1 to even next chunk
                        _rollingOverPushLength -= 31;
                        break;
                    default:
                        // set number of push bytes
                        _bufferChunk[0] = _rollingOverPushLength;
                        _rollingOverPushLength = 0;

                        // check if we have a push instruction in remaining code
                        // ignore the bytes we rolled over, they are not instructions
                        for (int i = _bufferChunk[0]; i < 31;)
                        {
                            byte instruction = currentChunk[i];
                            i++;
                            if (instruction is >= Push1 and <= Push32)
                            {
                                // we calculate data to ignore in code
                                i += instruction - PushOffset;

                                // check if we rolled over the chunk
                                _rollingOverPushLength = (byte)Math.Max(i - 31, 0);
                            }
                        }

                        break;
                }

                // move to next chunk
                _code = _code.Slice(31);
            }

            return true;
        }
    }

    public static byte[,] To2D(byte[][] jagged)
    {
        byte[,] keys = new byte[jagged.Length, 32];
        unsafe
        {
            for (int i = 0; i < jagged.Length; i++)
            {
                fixed (byte* pInKey = jagged[i])
                {
                    fixed (byte* pOutKey = &keys[i, 0])
                    {
                        Buffer.MemoryCopy(pInKey, pOutKey, 32, 32);
                    }
                }
            }
        }

        return keys;
    }

}
