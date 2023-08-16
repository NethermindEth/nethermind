// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core.Verkle;

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
