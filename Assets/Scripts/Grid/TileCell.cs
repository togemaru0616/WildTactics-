public class TileCell
{
    public int X { get; }
    public int Z { get; }
    public TerrainType Type { get; set; }
    public int BaseOwner { get; set; }
    public OutpostType Outpost { get; set; } = OutpostType.None;

    public TileCell(int x, int z)
    {
        X = x;
        Z = z;
    }
}
