// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.MessageProvider;

public sealed class FileMessageProvider(string filePath) : IMessageProvider<string>
{
    private readonly string _filePath = filePath;

    public IAsyncEnumerable<string> Messages(CancellationToken token = default)
    {
        FileInfo pathInfo = new(_filePath);
        if (pathInfo.Attributes.HasFlag(FileAttributes.Directory))
        {
            return Directory.GetFiles(_filePath)
                .OrderBy(filePath => filePath, StringComparer.OrdinalIgnoreCase)
                .Select(filePath => new FileInfo(filePath))
                .ToAsyncEnumerable()
                .SelectMany(info => File.ReadLinesAsync(info.FullName, token));
        }
        else
        {
            return File.ReadLinesAsync(_filePath, token);
        }
    }
}
