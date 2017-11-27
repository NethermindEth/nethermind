namespace Nevermind.Store
{
    public interface ISnapshotable
    {
        void Restore(int snapshot);

        void Commit();
        
        int TakeSnapshot();
    }
}