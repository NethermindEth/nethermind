// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Logging.NLog;

public partial class NLogManager
{
    public ILogger GetClassLogger<T>() => TypedLogger<T>.Logger;
}
