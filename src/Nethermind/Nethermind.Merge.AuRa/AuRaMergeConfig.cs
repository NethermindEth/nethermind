namespace Nethermind.Merge.AuRa
{
    public class AuRaMergeConfig : IAuRaMergeConfig
    {
        // No longer needed, but we can't remove it because backward compatibility. This settings is ignored
        public bool Enabled { get; set; }
    }
}
