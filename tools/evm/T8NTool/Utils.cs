using Nethermind.Core.Crypto;

namespace Evm.T8NTool;

public class Utils
{
    public static Hash256 ConvertToHash256(byte[] bytes)
    {
        var updatedBytes = new byte[32];
        Array.Copy(bytes, 0, updatedBytes, 32 - bytes.Length, bytes.Length);
        return new Hash256(updatedBytes);
    }
}