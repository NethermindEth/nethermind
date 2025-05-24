// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.SecretProvider;

public class FileSecretProvider : ISecretProvider
{
    private readonly string _filePath;

    public FileSecretProvider(string filePath)
    {
        _filePath = filePath;
    }

    private bool IsHex(char c) =>
        c is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';

    public string Secret
    {
        get
        {
            var content = File.ReadAllText(_filePath).Trim();
            if (!content.All(IsHex))
            {
                throw new ArgumentException($"{content} is not a Hex string");
            }

            return content;
        }
    }
}
