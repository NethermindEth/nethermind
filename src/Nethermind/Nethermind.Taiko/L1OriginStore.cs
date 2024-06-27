using System.Linq;
using System.Text;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Taiko;

public class L1OriginStore(IDb db) : IL1OriginStore
{
    private readonly IDb _db = db;

    // Database key prefix for L2 block's L1Origin.
    private static readonly byte[] l1OriginPrefix = Encoding.UTF8.GetBytes("TKO:L1O");
    private static readonly byte[] headL1OriginKey = Encoding.UTF8.GetBytes("TKO:LastL1O");

    // l1OriginKey calculates the L1Origin key.
    // l1OriginPrefix + l2HeaderHash -> l1OriginKey
    private static byte[] GetL1OriginKey(UInt256 blockId)
    {
        return [.. l1OriginPrefix, .. blockId.ToBigEndian()];
    }

    public L1Origin? ReadL1Origin(UInt256 blockId)
    {
        return _db.Get(GetL1OriginKey(blockId)) switch
        {
            null => null,
            byte[] bytes => Rlp.Decode<L1Origin>(bytes)
        };
    }

    public void WriteL1Origin(UInt256 blockid, L1Origin l1Origin)
    {
        _db.Set(GetL1OriginKey(blockid), Rlp.Encode(l1Origin).Bytes);
    }

    public UInt256? ReadHeadL1Origin()
    {
        return _db.Get(headL1OriginKey) switch
        {
            null => null,
            byte[] bytes => new UInt256(bytes, isBigEndian: true)
        };
    }

    public void WriteHeadL1Origin(UInt256 blockId)
    {
        _db.Set(headL1OriginKey, blockId.ToBigEndian());
    }
}
