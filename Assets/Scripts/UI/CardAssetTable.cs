using UnityEngine;

// AnimalType の int 値をインデックスとして使う (Fox=0 … Reindeer=15)
// AnimalRank の int 値をインデックスとして使う (B=0, A=1, S=2)
[CreateAssetMenu(menuName = "WildTactics/CardAssetTable")]
public class CardAssetTable : ScriptableObject
{
    [Header("Animal Sprites (index = (int)AnimalType)")]
    public Sprite[] animalSprites = new Sprite[16];

    [Header("Rank Badges (index = (int)AnimalRank : B=0 A=1 S=2)")]
    public Sprite[] rankBadges = new Sprite[3];

    [Header("Coin Badges (index = (int)AnimalRank : B=0 A=1 S=2)")]
    public Sprite[] coinBadges = new Sprite[3];

    [Header("Terrain Icons")]
    public Sprite terrainRocky;
    public Sprite terrainRockyBlocked;
    public Sprite terrainForest;
    public Sprite terrainForestBlocked;
    public Sprite terrainRiver;
    public Sprite terrainRiverBlocked;

    [Header("Prefabs")]
    public GameObject cardPrefab;
    public GameObject[] animalCardPrefabs = new GameObject[16];

    static CardAssetTable _instance;
    public static CardAssetTable Instance
    {
        get
        {
            if (_instance == null)
                _instance = Resources.Load<CardAssetTable>("CardAssetTable");
            return _instance;
        }
    }

    public Sprite GetAnimalSprite(AnimalType type)
    {
        int i = (int)type;
        return i >= 0 && i < animalSprites.Length ? animalSprites[i] : null;
    }

    public Sprite GetRankBadge(AnimalRank rank)
    {
        int i = (int)rank;
        return i >= 0 && i < rankBadges.Length ? rankBadges[i] : null;
    }

    public Sprite GetCoinBadge(AnimalRank rank)
    {
        int i = (int)rank;
        return i >= 0 && i < coinBadges.Length ? coinBadges[i] : null;
    }

    public Sprite GetTerrainSprite(TerrainType type, bool blocked) => type switch
    {
        TerrainType.Rocky  => blocked ? terrainRockyBlocked  : terrainRocky,
        TerrainType.Forest => blocked ? terrainForestBlocked : terrainForest,
        _                  => blocked ? terrainRiverBlocked  : terrainRiver,
    };

    public GameObject GetCardPrefab(AnimalType type)
    {
        int i = (int)type;
        if (i >= 0 && i < animalCardPrefabs.Length && animalCardPrefabs[i] != null)
            return animalCardPrefabs[i];
        return cardPrefab;
    }
}
