// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Xdc;

public class SyncInfoTypes
{
    public Hash256? Hash { get; set; }
    public int QCSigners { get; set; }
    public int TCSigners { get; set; }
}
