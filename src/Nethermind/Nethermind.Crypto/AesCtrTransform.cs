// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Security.Cryptography;

namespace Nethermind.Crypto;

internal sealed class AesCtrTransform : ICryptoTransform
{
    private readonly ICryptoTransform _aes;
    private readonly byte[] _counter;
    private readonly byte[] _iv;

    public AesCtrTransform(ICryptoTransform aes, ReadOnlySpan<byte> iv)
    {
        _aes = aes ?? throw new ArgumentNullException(nameof(aes));
        _counter = iv.ToArray();
        _iv = iv.ToArray();
    }

    public bool CanReuseTransform => _aes.CanReuseTransform;

    public bool CanTransformMultipleBlocks => _aes.CanTransformMultipleBlocks;

    public int InputBlockSize => _aes.InputBlockSize;

    public int OutputBlockSize => _aes.OutputBlockSize;

    public void Dispose() => _aes.Dispose();

    public int TransformBlock(byte[] inputBuffer, int inputOffset, int inputCount, byte[] outputBuffer, int outputOffset)
    {
        ArgumentNullException.ThrowIfNull(inputBuffer);
        ArgumentNullException.ThrowIfNull(outputBuffer);

        if (inputOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(inputOffset));

        if (inputCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputCount));

        if (inputCount % InputBlockSize != 0)
            throw new ArgumentOutOfRangeException(nameof(inputCount), "TransformBlock may only process bytes in block sized increments.");

        if (inputOffset + inputCount > inputBuffer.Length)
            throw new ArgumentOutOfRangeException(nameof(inputOffset), "Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");

        if (outputOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(outputOffset));

        if (outputOffset + OutputBlockSize > outputBuffer.Length)
            throw new ArgumentOutOfRangeException(nameof(outputOffset), "Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");

        var counterLen = _counter.Length;
        var counterOutput = new byte[counterLen];
        var outputLen = _aes.TransformBlock(_counter, 0, counterLen, counterOutput, 0);

        for (var i = 0; i < counterLen; i++)
            outputBuffer[outputOffset + i] = (byte)(inputBuffer[inputOffset + i] ^ counterOutput[i]);

        for (var i = counterLen; --i >= 0 && ++_counter[i] == 0;) { }

        return outputLen;
    }

    public byte[] TransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount)
    {
        ArgumentNullException.ThrowIfNull(inputBuffer);

        if (inputOffset < 0)
            throw new ArgumentOutOfRangeException(nameof(inputOffset));

        if (inputCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(inputCount));

        if (inputOffset + inputCount > inputBuffer.Length)
            throw new ArgumentOutOfRangeException(nameof(inputOffset), "Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");

        var blockSize = InputBlockSize;
        var counterOutput = new byte[blockSize];
        var offset = 0;
        var outputBuffer = new byte[inputCount];

        for (var i = 0; i + blockSize <= inputCount; i += blockSize)
            offset += TransformBlock(inputBuffer, inputOffset + i, blockSize, outputBuffer, offset);

        _aes.TransformBlock(_counter, 0, blockSize, counterOutput, 0);

        for (int i = 0, count = inputCount % blockSize; i < count; i++)
            outputBuffer[offset + i] = (byte)(inputBuffer[inputOffset + offset + i] ^ counterOutput[i]);

        Reset();

        return outputBuffer;
    }

    private void Reset() => _iv.CopyTo((Memory<byte>)_counter);
}
