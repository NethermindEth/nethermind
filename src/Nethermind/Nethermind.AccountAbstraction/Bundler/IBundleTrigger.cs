using System;

namespace Nethermind.AccountAbstraction.Bundler
{
    public interface IBundleTrigger
    {
        event EventHandler<EventArgs>? TriggerBundle;
    }
}
