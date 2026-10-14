// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Collections;

public sealed partial class JournalCollection<T>
{
    public partial void Clear() => _list.Clear();
}
