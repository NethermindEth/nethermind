namespace Nethermind.Monitoring.Metrics
{
    public interface IMetricsUpdater
    {
        void StartUpdating();
        void StopUpdating();
    }
}