// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.Handlers;

public class UpdatePayloadWithInclusionListHandler()
    : IHandler<(string payloadId, byte[][] inclusionListTransactions), string?>
{
    public ResultWrapper<string?> Handle((string payloadId, byte[][] inclusionListTransactions) _)
    {
        return ResultWrapper<string?>.Success(null);
    }
}
