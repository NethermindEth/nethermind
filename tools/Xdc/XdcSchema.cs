// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Xdc;

// from schema.go
public static class XdcSchema
{
    public static readonly byte[] HeadHeaderKey = "LastHeader"u8.ToArray();
    public static readonly byte[] HeaderPrefix = "h"u8.ToArray();
    public static readonly byte[] HeaderNumberPrefix = "H"u8.ToArray();
    public static readonly byte[] CodePrefix = "c"u8.ToArray();
}
