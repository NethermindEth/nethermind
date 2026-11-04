// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network;

internal static class RlpReaderMessageExtensions
{
    /// <summary>
    /// Returns <paramref name="message"/> if <paramref name="ctx"/> has read all of its data, or disposes it and throws.
    /// </summary>
    /// <exception cref="RlpException">Data is left unread.</exception>
    public static T CheckEnd<T>(this ref RlpReader ctx, T message) where T : IDisposable
    {
        if (ctx.Position != ctx.Length)
        {
            message.Dispose();
            ctx.CheckEnd();
        }

        return message;
    }
}
