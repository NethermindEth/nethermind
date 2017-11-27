namespace Nevermind.Store
{
    public interface IMultiDb : ISnapshotable
    {
        IDb CreateDb();
    }
}