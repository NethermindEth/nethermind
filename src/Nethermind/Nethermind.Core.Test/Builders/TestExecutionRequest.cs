// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;

namespace Nethermind.Core.Test.Builders;

public class TestExecutionRequest : ExecutionRequest.ExecutionRequest
{
    private byte[][]? _requestDataParts;

    public byte[][]? RequestDataParts
    {
        get => _requestDataParts;
        set
        {
            _requestDataParts = value;
            RequestData = value is null ? null : Bytes.Concat(value);
        }
    }
}
