using Paprika.Data;

namespace Paprika.Store;

public interface IPageVisitor
{
    IDisposable On<TPage>(scoped ref NibblePath.Builder prefix, TPage page, DbAddress addr)
        where TPage : unmanaged, IPage;

    IDisposable On<TPage>(TPage page, DbAddress addr)
        where TPage : unmanaged, IPage;

    /// <summary>
    /// Just a named scope, to group pages.
    /// </summary>
    IDisposable Scope(string name);
}

public interface IVisitable
{
    void Accept(IPageVisitor visitor);
}
