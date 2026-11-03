namespace Paprika.Store;

/// <summary>
/// A way of clearing the given component if it's allocated over dirty memory,
/// without actually calling the costly clear of <see cref="IBatchContext.GetNewPage"/>.
/// </summary>
public interface IClearable
{
    /// <summary>
    /// Clears the component without the need of zeroing whole memory.
    /// </summary>
    void Clear();
}
