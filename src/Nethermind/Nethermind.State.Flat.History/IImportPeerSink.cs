// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Local seam standing in for a not-yet-built peer-management surface: today's <see cref="IWindowImportSource"/>
/// has no notion of peer identity or scoring at all. Modeling each distinct <see cref="IWindowImportSource"/>
/// instance as one feed (one peer connection, one era-file mirror, whatever the transport turns out to be) lets
/// <see cref="PeerFedWindowImporter"/> ban a source that served a corrupt sub-range (per
/// <see cref="WindowImportVerifier"/>'s verdict — this type carries no verification logic of its own, only
/// ban/alternate-selection policy) and select a different one to refetch from, without inventing real devp2p peer
/// plumbing here. The devp2p peer pool is expected to supply the production implementation once it exists.
/// </summary>
public interface IImportPeerSink
{
    void BanSource(IWindowImportSource source, string reason);

    bool TryGetAlternateSource(IWindowImportSource banned, [NotNullWhen(true)] out IWindowImportSource? alternate);
}
