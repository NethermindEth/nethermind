// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.MessageProvider;

public class FileMessageProvider : IMessageProvider<string>
{
    private readonly string _filePath;

    public FileMessageProvider(string filePath)
    {
        _filePath = filePath;
    }


    public IAsyncEnumerable<string> Messages
    {
        get
        {
            var pathInfo = new FileInfo(_filePath);
            if (pathInfo.Attributes.HasFlag(FileAttributes.Directory))
            {
                return Directory.GetFiles(_filePath)
                    .Select(filePath => new FileInfo(filePath))
                    .OrderBy(info => info.LastWriteTime)
                    .ToAsyncEnumerable()
                    .SelectMany(info => File.ReadLinesAsync(info.FullName));
            }

            if (pathInfo.Attributes.HasFlag(FileAttributes.Normal))
            {
                return File.ReadLinesAsync(_filePath);
            }

            throw new ArgumentException("Path is neither a Folder or a File", nameof(_filePath));
        }
    }
}
