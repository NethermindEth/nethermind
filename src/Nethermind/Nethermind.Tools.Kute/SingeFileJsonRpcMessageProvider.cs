// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute;

class SingeFileJsonRpcMessageProvider : IJsonRpcMessageProvider
{
    private readonly string _filePath;

    public SingeFileJsonRpcMessageProvider(Config config)
    {
        _filePath = config.MessagesFile;
    }

    public IAsyncEnumerable<string> Messages => File.ReadLinesAsync(_filePath);
}
