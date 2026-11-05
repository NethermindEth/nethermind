// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.IO;

namespace Nethermind.Core.Resettables;

public class RecyclableStream
{
    private static readonly RecyclableMemoryStreamManager _manager = new();
    public static RecyclableMemoryStream GetStream(string tag) => _manager.GetStream(tag);

    /// <summary>
    /// Gets a recyclable stream with an initial size hint.
    /// </summary>
    /// <param name="tag">A diagnostic tag identifying the stream owner.</param>
    /// <param name="requiredSize">The expected stream size in bytes.</param>
    /// <returns>A recyclable memory stream.</returns>
    public static RecyclableMemoryStream GetStream(string tag, long requiredSize) => _manager.GetStream(tag, requiredSize);
}
