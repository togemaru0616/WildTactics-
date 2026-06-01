using UnityEngine;

// テクスチャのインデックスは (int)TerrainType と一致させる
// 0=Flat 1=Rocky 2=Forest 3=River 4=Bridge 5=Pond 6=Sand
[CreateAssetMenu(menuName = "WildTactics/TileAssetTable")]
public class TileAssetTable : ScriptableObject
{
    public Texture2D[] terrainTextures = new Texture2D[7];

    public Texture2D lighthouse;
    public Texture2D campGrass;
    public Texture2D campForest;
    public Texture2D gameBoard;

    static TileAssetTable _instance;
    public static TileAssetTable Instance
    {
        get
        {
            if (_instance == null)
                _instance = Resources.Load<TileAssetTable>("TileAssetTable");
            return _instance;
        }
    }

    public Texture2D GetTerrainTex(TerrainType type)
    {
        int i = (int)type;
        return i >= 0 && i < terrainTextures.Length ? terrainTextures[i] : null;
    }
}
