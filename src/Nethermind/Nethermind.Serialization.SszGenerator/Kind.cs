public enum Kind
{
    None = 0x0,
    Basic = 0x1,
    Container = 0x2,

    Vector = 0x4,
    List = 0x8,

    BitVector = 0x10,
    BitList = 0x20,

    Union = 0x40,

    Collection = Vector | List,
}
