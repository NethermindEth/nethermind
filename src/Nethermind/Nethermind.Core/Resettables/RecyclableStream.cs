// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.IO;

namespace Nethermind.Core.Resettables;

public class RecyclableStream
{
    private static readonly RecyclableMemoryStreamManager _manager = new RecyclableMemoryStreamManager();
    public static RecyclableMemoryStream GetStream(string tag) => _manager.GetStream(tag);
}
