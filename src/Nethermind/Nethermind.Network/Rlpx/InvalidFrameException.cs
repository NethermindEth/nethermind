// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Network.Rlpx;

/// <summary>A received RLPx frame or packet breaks the framing rules.</summary>
/// <remarks>Carries either a message of its own, or the parser exception that rejected the frame.</remarks>
internal sealed class InvalidFrameException : Exception
{
    public InvalidFrameException(string message) : base(message)
    {
    }

    public InvalidFrameException(Exception innerException) : base(innerException.Message, innerException)
    {
    }
}
