// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.State.Flat.History;

public interface IArchiveClonePeerSink
{
    void BanSource(IArchiveCloneSource source, string reason);

    bool TryGetAlternateSource(IArchiveCloneSource banned, [NotNullWhen(true)] out IArchiveCloneSource? alternate);
}
