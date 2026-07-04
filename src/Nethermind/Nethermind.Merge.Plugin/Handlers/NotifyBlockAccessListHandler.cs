// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// EIP-8146 <c>engine_notifyBlockAccessListV1</c>: accepts an RLP-encoded block access list
/// sidecar ahead of the matching payload so the payload can execute without waiting for the
/// BAL to arrive in its envelope.
/// </summary>
public class NotifyBlockAccessListHandler(IBlockAccessListSidecarStore sidecarStore, ILogManager logManager)
    : IHandler<byte[], string?>
{
    private readonly ILogger _logger = logManager.GetClassLogger<NotifyBlockAccessListHandler>();

    public ResultWrapper<string?> Handle(byte[] rlpEncodedBal)
    {
        if (rlpEncodedBal is null || rlpEncodedBal.Length == 0)
        {
            return ResultWrapper<string?>.Fail("Block access list must not be empty", ErrorCodes.InvalidParams);
        }

        try
        {
            Rlp.Decode<ReadOnlyBlockAccessList>(rlpEncodedBal);
        }
        catch (RlpException e)
        {
            if (_logger.IsDebug) _logger.Debug($"Rejected malformed block access list sidecar: {e.Message}");
            return ResultWrapper<string?>.Fail($"Error decoding block access list: {e.Message}", ErrorCodes.InvalidParams);
        }

        sidecarStore.Add(rlpEncodedBal);
        return ResultWrapper<string?>.Success(null);
    }
}
