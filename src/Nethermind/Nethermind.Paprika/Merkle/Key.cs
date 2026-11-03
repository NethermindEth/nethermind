// ReSharper disable once CheckNamespace

using System.Diagnostics;

namespace Paprika.Data;

public readonly ref partial struct Key
{
    [DebuggerStepThrough]
    public static Key Merkle(NibblePath path) => new(path, DataType.Merkle, NibblePath.Empty);
}
