// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using DotNetty.Codecs;
using DotNetty.Transport.Channels;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Snappier;

namespace Nethermind.Network.Rlpx;

public class SnappyDecoder(ILogger logger) : MessageToMessageDecoder<Packet>
{
    protected override void Decode(IChannelHandlerContext context, Packet message, List<object> output)
    {
        if (Snappy.GetUncompressedLength(message.Data) > SnappyParameters.MaxSnappyLength)
        {
            throw new Exception("Max message size exceeded");
        }

        if (message.Data.Length > SnappyParameters.MaxSnappyLength / 4)
        {
            if (logger.IsWarn) logger.Warn($"Big Snappy message of length {message.Data.Length}");
        }
        else
        {
            if (logger.IsTrace) logger.Trace($"Decompressing with Snappy a message of length {message.Data.Length}");
        }

        try
        {
            message.Data = Snappy.DecompressToArray(message.Data);
        }
        catch
        {
            logger.Error($"{message.Data.ToHexString()}");
            throw;
        }

        output.Add(message);
    }
}
