public enum Kind
{
    None = 0x0,
    Basic = 0x1,
    Container = 0x2,
    ProgressiveContainer = 0x4,

    Vector = 0x8,
    List = 0x10,
    ProgressiveList = 0x20,

    BitVector = 0x40,
    BitList = 0x80,
    ProgressiveBitList = 0x100,

    CompatibleUnion = 0x200,

    Collection = Vector | List | ProgressiveList,
}
