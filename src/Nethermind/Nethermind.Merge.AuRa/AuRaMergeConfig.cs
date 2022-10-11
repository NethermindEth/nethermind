namespace Nethermind.Merge.AuRa
{
    public class AuRaMergeConfig : IAuRaMergeConfig
    {
        // No longer needed, but we can remove it because backward compatibility. This settings is ignored
        public bool Enabled { get; set; }
    }
}
