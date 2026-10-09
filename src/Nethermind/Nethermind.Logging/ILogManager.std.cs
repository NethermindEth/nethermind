// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Logging;

public partial interface ILogManager
{
    ILogger GetClassLogger<T>();
}
