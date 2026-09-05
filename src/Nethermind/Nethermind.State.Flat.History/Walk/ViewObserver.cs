// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.History.Walk;

internal abstract class ViewObserver
{
    public virtual bool ObservesEveryBlock => false;

    public virtual bool OnBlock(ulong block, in NodeView view) => true;

    public virtual void OnChanged(ulong block, in NodeView view)
    {
    }
}
