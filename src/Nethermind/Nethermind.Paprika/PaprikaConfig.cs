namespace Nethermind.Paprika;

/// <summary>
/// One slot takes ~100 bytes of managed + unmanaged memory.
/// This multiplied by 10,000 entries, gives 1MGB per block of total budget to cache things.
/// </summary>
public class PaprikaConfig : IPaprikaConfig
{
    public ushort CacheStateBeyond { get; set; } = 8;

    public int CacheStatePerBlock { get; set; } = 5000;

    public ushort CacheMerkleBeyond { get; set; } = 8;

    public int CacheMerklePerBlock { get; set; } = 5000;

    public bool ParallelMerkle { get; set; } = true;
}
