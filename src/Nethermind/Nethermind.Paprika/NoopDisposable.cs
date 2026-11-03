namespace Paprika;

/// <summary>
/// An <see cref="IDisposable"/> that does nothing.
/// </summary>
public sealed class NoopDisposable : IDisposable
{
    private NoopDisposable() { }

    public static readonly IDisposable Instance = new NoopDisposable();

    public void Dispose()
    {

    }
}

/// <summary>
/// An <see cref="IDisposable"/> that allows composing disposables.
/// </summary>
public sealed class CompositeDisposable<T> : IDisposable
    where T : IDisposable
{
    private readonly List<T> _items = new();

    public void Add(T disposable) => _items.Add(disposable);

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    public void Dispose()
    {
        foreach (var disposable in _items)
        {
            disposable.Dispose();
        }
    }
}
