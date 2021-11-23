using System;
using System.Timers;

namespace Nethermind.AccountAbstraction.Bundler
{
    public class TxBundleRegularlyTrigger : ITxBundlingTrigger
    {
        private readonly Timer _timer;

        public TxBundleRegularlyTrigger() : this(TimeSpan.FromSeconds(2)) { }

        public TxBundleRegularlyTrigger(TimeSpan interval)
        {
            _timer = new Timer(interval.TotalMilliseconds);
            _timer.Elapsed += TimerOnElapsed;
            _timer.AutoReset = false;
            _timer.Start();
        }

        private void TimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            TriggerTxBundling?.Invoke(this, new EventArgs());
            _timer.Enabled = true;
        }

        public event EventHandler<EventArgs>? TriggerTxBundling;

        public void Dispose()
        {
            _timer.Dispose();
        }
    }
}
