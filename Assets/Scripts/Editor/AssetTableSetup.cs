using UnityEngine;
using UnityEditor;
using System.IO;

public static class AssetTableSetup
{
    [MenuItem("WildTactics/Setup Asset Tables")]
    public static void SetupAll()
    {
        SetupTileAssetTable();
        SetupSoundAssetTable();
        SetupUIAssetTable();
        SetupCardAssetTable();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[AssetTableSetup] Complete.");
    }

    // ---- ユーティリティ ----

    static T GetOrCreate<T>(string resourceName) where T : ScriptableObject
    {
        string path = $"Assets/Resources/{resourceName}.asset";
        var asset = AssetDatabase.LoadAssetAtPath<T>(path);
        if (asset != null) return asset;
        asset = ScriptableObject.CreateInstance<T>();
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        AssetDatabase.CreateAsset(asset, path);
        return asset;
    }

    // Assets/Resources/{resourcesPath}.{ext} を試す（拡張子なしで渡す）
    static T Load<T>(string resourcesPath) where T : Object
    {
        string base_ = $"Assets/Resources/{resourcesPath}";
        string[] exts = typeof(T) == typeof(Font)
            ? new[] { ".ttf", ".otf" }
            : typeof(T) == typeof(AudioClip)
            ? new[] { ".wav", ".ogg", ".mp3" }
            : typeof(T) == typeof(GameObject)
            ? new[] { ".prefab" }
            : new[] { ".png", ".jpg", ".jpeg", ".tga", ".psd" };

        foreach (var ext in exts)
        {
            var loaded = AssetDatabase.LoadAssetAtPath<T>(base_ + ext);
            if (loaded != null) return loaded;
        }

        // Sprite は sub-asset の場合があるので全サブアセットを確認
        if (typeof(T) == typeof(Sprite))
        {
            foreach (var ext in new[] { ".png", ".jpg" })
            {
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(base_ + ext))
                    if (o is T t) return t;
            }
        }

        Debug.LogWarning($"[AssetTableSetup] Not found: {base_}");
        return null;
    }

    // ---- 各テーブル ----

    static void SetupTileAssetTable()
    {
        var t = GetOrCreate<TileAssetTable>("TileAssetTable");
        if (t.terrainTextures == null || t.terrainTextures.Length < 7)
            t.terrainTextures = new Texture2D[7];

        // インデックスは (int)TerrainType と一致: Flat=0 Rocky=1 Forest=2 River=3 Bridge=4 Pond=5 Sand=6
        t.terrainTextures[0] = Load<Texture2D>("Tiles/Grass");
        t.terrainTextures[1] = Load<Texture2D>("Tiles/Rocky");
        t.terrainTextures[2] = Load<Texture2D>("Tiles/Forest");
        t.terrainTextures[3] = Load<Texture2D>("Tiles/River");
        t.terrainTextures[4] = Load<Texture2D>("Tiles/Bridge");
        t.terrainTextures[5] = Load<Texture2D>("Tiles/Pond");
        t.terrainTextures[6] = Load<Texture2D>("Tiles/Sand");
        t.lighthouse  = Load<Texture2D>("Tiles/Lighthouse");
        t.campGrass   = Load<Texture2D>("Tiles/CampGrass");
        t.campForest  = Load<Texture2D>("Tiles/CampForest");
        t.gameBoard   = Load<Texture2D>("Tiles/GameBoard");

        EditorUtility.SetDirty(t);
    }

    static void SetupSoundAssetTable()
    {
        var t = GetOrCreate<SoundAssetTable>("SoundAssetTable");

        t.bgmTitle     = Load<AudioClip>("Sounds/BGM/BGM_Title");
        t.bgmPlacement = Load<AudioClip>("Sounds/BGM/BGM_Placement");
        t.bgmExplore   = Load<AudioClip>("Sounds/BGM/BGM_Explore");
        t.bgmCombat    = Load<AudioClip>("Sounds/BGM/BGM_Combat");
        t.bgmVictory   = Load<AudioClip>("Sounds/BGM/BGM_Victory");
        t.bgmDefeat    = Load<AudioClip>("Sounds/BGM/BGM_Defeat");

        t.seTap         = Load<AudioClip>("Sounds/SE/SE_Tap");
        t.seCancel      = Load<AudioClip>("Sounds/SE/SE_Cancel");
        t.seAnimalTouch = Load<AudioClip>("Sounds/SE/SE_AnimalTouch");
        t.seHeal        = Load<AudioClip>("Sounds/SE/SE_Heal");
        t.sePoison      = Load<AudioClip>("Sounds/SE/SE_Poison");
        t.sePoisonBack  = Load<AudioClip>("Sounds/SE/SE_PoisonBack");

        EditorUtility.SetDirty(t);
    }

    static void SetupUIAssetTable()
    {
        var t = GetOrCreate<UIAssetTable>("UIAssetTable");

        t.font            = Load<Font>("Fonts/NotoSansJP-Regular");
        t.SetShaders(Shader.Find("Universal Render Pipeline/Lit"),
                     Shader.Find("Universal Render Pipeline/Unlit"));
        t.titleBackground = Load<Texture2D>("UI/Title/Title");
        t.loadingScreen   = Load<Texture2D>("UI/Loading");
        t.victory         = Load<Texture2D>("UI/VICTORY");
        t.defeat          = Load<Texture2D>("UI/DEFEAT");
        t.cooldownSpinner = Load<Texture2D>("UI/UI_Loading");

        EditorUtility.SetDirty(t);
    }

    static void SetupCardAssetTable()
    {
        var t = GetOrCreate<CardAssetTable>("CardAssetTable");
        if (t.animalSprites     == null || t.animalSprites.Length     < 16) t.animalSprites     = new Sprite[16];
        if (t.animalCardPrefabs == null || t.animalCardPrefabs.Length < 16) t.animalCardPrefabs = new GameObject[16];
        if (t.rankBadges        == null || t.rankBadges.Length        <  3) t.rankBadges        = new Sprite[3];
        if (t.coinBadges        == null || t.coinBadges.Length        <  3) t.coinBadges        = new Sprite[3];

        // 動物スプライットと動物別Prefab（インデックス = (int)AnimalType）
        var animalNames = System.Enum.GetNames(typeof(AnimalType));
        for (int i = 0; i < animalNames.Length && i < 16; i++)
        {
            t.animalSprites[i]     = Load<Sprite>($"Cards/{animalNames[i]}");
            t.animalCardPrefabs[i] = Load<GameObject>($"Prefabs/Cards/Card_{animalNames[i]}");
        }

        t.cardPrefab = Load<GameObject>("Prefabs/CardPrefab");

        // ランクバッジ（インデックス = (int)AnimalRank: B=0 A=1 S=2）
        var rankNames = System.Enum.GetNames(typeof(AnimalRank));
        for (int i = 0; i < rankNames.Length && i < 3; i++)
            t.rankBadges[i] = Load<Sprite>($"UI/Cards/Rank_{rankNames[i]}");

        // コインバッジ（B=0→Coin_5, A=1→Coin_10, S=2→Coin_20）
        int[] costs = { 5, 10, 20 };
        for (int i = 0; i < costs.Length && i < 3; i++)
            t.coinBadges[i] = Load<Sprite>($"UI/Cards/Coin_{costs[i]}");

        // 地形アイコン
        t.terrainRocky         = Load<Sprite>("UI/Terrain/Terrain_Rocky");
        t.terrainRockyBlocked  = Load<Sprite>("UI/Terrain/Terrain_Rocky_Blocked");
        t.terrainForest        = Load<Sprite>("UI/Terrain/Terrain_Forest");
        t.terrainForestBlocked = Load<Sprite>("UI/Terrain/Terrain_Forest_Blocked");
        t.terrainRiver         = Load<Sprite>("UI/Terrain/Terrain_River");
        t.terrainRiverBlocked  = Load<Sprite>("UI/Terrain/Terrain_River_Blocked");

        EditorUtility.SetDirty(t);
    }
}
