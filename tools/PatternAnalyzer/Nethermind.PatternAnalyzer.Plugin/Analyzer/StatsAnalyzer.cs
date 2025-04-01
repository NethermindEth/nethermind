public abstract class StatsAnalyzer<T>
{
    public virtual void Add(IEnumerable<T> items)
    {
    }

    public virtual void Add(T item)
    {
    }
}
