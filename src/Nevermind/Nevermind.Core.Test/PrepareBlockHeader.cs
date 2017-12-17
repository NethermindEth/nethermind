namespace Nevermind.Core.Test
{
    public static class PrepareBlockHeader
    {
        public static BlockHeaderBuilder BlockHeader(this Prepare prepare)
        {
            return new BlockHeaderBuilder();
        }
    }
}