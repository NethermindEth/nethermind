using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.Tracing.GethStyle.JavaScript;
using Nethermind.Int256;

namespace Evm.T8NTool;

public class Utils
{
    public static Hash256 ConvertToHash256(byte[] bytes)
    {
        var updatedBytes = new byte[32];
        Array.Copy(bytes, 0, updatedBytes, 32 - bytes.Length, bytes.Length);
        return new Hash256(updatedBytes);
    }
    
    public static Hash256 ConvertToHash256(UInt256 value)
    {
        return ConvertToHash256(Bytes.FromHexString(value.ToHexString(true)));
    }
}