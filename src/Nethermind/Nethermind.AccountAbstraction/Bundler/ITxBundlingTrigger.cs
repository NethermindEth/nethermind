using System;

namespace Nethermind.AccountAbstraction.Bundler
{
    public interface ITxBundlingTrigger
    {
        event EventHandler<EventArgs>? TriggerTxBundling;
    }
}
