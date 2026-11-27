// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Network.Rlpx;

/// <summary>Authenticates and decrypts received RLPx frames, over memory the caller provides.</summary>
/// <remarks>
/// The caller keeps the bytes of a frame, including one that arrives in pieces, and calls the steps in order: the header,
/// then the payload's blocks, then the payload MAC. RLPx has a single MAC over the whole payload ciphertext, so the caller
/// must not pass a frame on before <see cref="CheckPayloadMac"/> succeeds. Absorbing and decrypting are separate steps so
/// that a frame that arrived whole can be authenticated before it is decrypted in place.
/// </remarks>
internal sealed class FrameDecoder(IFrameCipher cipher, IFrameMacProcessor macProcessor, int maxFrameSize)
{
    private readonly IFrameCipher _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
    private readonly IFrameMacProcessor _macProcessor = macProcessor ?? throw new ArgumentNullException(nameof(macProcessor));

    /// <summary>Authenticates and decrypts a frame header, and checks the frame size it declares.</summary>
    /// <param name="header">The 16 bytes of the encrypted header.</param>
    /// <param name="mac">The 16 bytes of the header MAC.</param>
    /// <param name="plaintext">Receives the decrypted header.</param>
    /// <returns>The length of the frame's payload with its padding.</returns>
    /// <exception cref="InvalidFrameException">The MAC does not match, or the frame is empty or larger than the maximum.</exception>
    public int ReadHeader(ReadOnlySpan<byte> header, ReadOnlySpan<byte> mac, Span<byte> plaintext)
    {
        _macProcessor.UpdateIngressMac(header, true);
        if (!_macProcessor.CheckMac(mac, true))
            ThrowInvalidMac("header");

        _cipher.Decrypt(header, plaintext);

        int payloadSize = plaintext[0] & 0xFF;
        payloadSize = (payloadSize << 8) + (plaintext[1] & 0xFF);
        payloadSize = (payloadSize << 8) + (plaintext[2] & 0xFF);

        if (payloadSize is 0)
            ThrowZeroSizeFrame();

        if (payloadSize > maxFrameSize)
            ThrowFrameTooLarge(payloadSize, maxFrameSize);

        return payloadSize + Frame.CalculatePadding(payloadSize);
    }

    /// <summary>Absorbs whole blocks of payload ciphertext into the ingress MAC.</summary>
    public void AbsorbPayload(ReadOnlySpan<byte> ciphertext) => _macProcessor.UpdateIngressMac(ciphertext, false);

    /// <summary>Decrypts whole blocks of payload ciphertext.</summary>
    /// <remarks><paramref name="plaintext"/> may be the same memory as <paramref name="ciphertext"/>.</remarks>
    public void DecryptPayload(ReadOnlySpan<byte> ciphertext, Span<byte> plaintext) => _cipher.Decrypt(ciphertext, plaintext);

    /// <summary>Checks the payload MAC once the whole payload ciphertext has been absorbed.</summary>
    /// <exception cref="InvalidFrameException">The MAC does not match.</exception>
    public void CheckPayloadMac(ReadOnlySpan<byte> mac)
    {
        if (!_macProcessor.CheckMac(mac, false))
            ThrowInvalidMac("payload");
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowZeroSizeFrame()
        => throw new InvalidFrameException("Frame payload is empty");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowFrameTooLarge(int payloadSize, int maxFrameSize)
        => throw new InvalidFrameException($"Frame payload too large: {payloadSize} bytes, max {maxFrameSize} bytes");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowInvalidMac(string section)
        => throw new InvalidFrameException($"Sender delivered a frame with an invalid {section} MAC");
}
