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


    // TODO: Check if file contains a Hex value
    public string Secret => File.ReadAllText(_filePath).Trim();
}
