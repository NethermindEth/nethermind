// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute;

class FileJsonRpcMessageProvider : IJsonRpcMessageProvider
{
    private readonly string _path;

    public FileJsonRpcMessageProvider(Config config)
    {
        _path = config.MessagesSourcePath;
    }

    public IAsyncEnumerable<string> Messages
    {
        get
        {
            var pathInfo = new FileInfo(_path);
            if (pathInfo.Attributes.HasFlag(FileAttributes.Directory))
            {
                return Directory.GetFiles(_path)
                    .Select(filePath => new FileInfo(filePath))
                    .OrderBy(info => info.LastWriteTime)
                    .ToAsyncEnumerable()
                    .SelectMany(info => File.ReadLinesAsync(info.FullName));
            }

            if (pathInfo.Attributes.HasFlag(FileAttributes.Normal))
            {
                return File.ReadLinesAsync(_path);
            }

            throw new ArgumentException("Path is neither a Folder or a File", nameof(_path));
        }
    }
}
