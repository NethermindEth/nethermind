// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Codecs;

namespace Nethermind.Network.Rlpx;

internal static class NettyFrameExceptions
{
    /// <summary>Gives the exception the DotNetty handlers throw for an invalid frame: the same message, or the same inner exception.</summary>
    /// <remarks>
    /// DotNetty wraps any other exception from a decoder in a <see cref="DecoderException"/>, which would change the type the
    /// handlers log and record in the disconnect details.
    /// </remarks>
    public static CorruptedFrameException ToCorruptedFrameException(this InvalidFrameException exception) =>
        exception.InnerException is { } innerException
            ? new CorruptedFrameException(innerException)
            : new CorruptedFrameException(exception.Message);
}
