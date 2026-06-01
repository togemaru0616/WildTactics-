using UnityEditor;
using UnityEngine;

public static class AudioWebGLOptimizer
{
    // -------------------------------------------------------
    //  音声
    // -------------------------------------------------------

    [MenuItem("Tools/WebGL Optimize/1. Apply Audio Settings")]
    static void ApplyAudio()
    {
        int count = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/Resources/Sounds" }))
        {
            string path     = AssetDatabase.GUIDToAssetPath(guid);
            var    importer = AssetImporter.GetAtPath(path) as AudioImporter;
            if (importer == null) continue;

            bool isBGM = path.Contains("/BGM/");

            var settings = importer.defaultSampleSettings;
            settings.sampleRateSetting  = AudioSampleRateSetting.OverrideSampleRate;
            settings.sampleRateOverride = 22050;
            settings.compressionFormat  = AudioCompressionFormat.Vorbis;

            if (isBGM)
            {
                settings.loadType         = AudioClipLoadType.CompressedInMemory;
                settings.preloadAudioData = true;
                settings.quality          = 0.5f;
                importer.forceToMono      = false;
            }
            else
            {
                // SE・動物SE：Decompress On Load（即時再生）+ モノラル化
                settings.loadType    = AudioClipLoadType.DecompressOnLoad;
                settings.quality     = 0.7f;
                importer.forceToMono = true;
            }

            importer.defaultSampleSettings = settings;
            importer.SaveAndReimport();
            count++;
        }
        Debug.Log($"[WebGL Optimize] 音声: {count} 件に適用しました。");
    }

    // -------------------------------------------------------
    //  テクスチャ
    // -------------------------------------------------------

    [MenuItem("Tools/WebGL Optimize/2. Apply Texture Settings")]
    static void ApplyTextures()
    {
        int count = 0;

        // カード絵（最大表示 ~780px → 1024 で十分）
        count += ApplyTextureFolder("Assets/Resources/Cards",       1024, true);

        // UI パーツ（フレーム・バッジ・アイコン類）
        count += ApplyTextureFolder("Assets/Resources/UI/Cards",    512,  true);
        count += ApplyTextureFolder("Assets/Resources/UI/Terrain",  256,  true);
        count += ApplyTextureFolder("Assets/Resources/UI/Title",    512,  true);
        count += ApplyTextureFolder("Assets/Resources/UI",          512,  true);

        // タイルテクスチャ（グリッドの小さいセルに表示）
        count += ApplyTextureFolder("Assets/Resources/Tiles",       512,  false);

        Debug.Log($"[WebGL Optimize] テクスチャ: {count} 件に WebGL オーバーライドを設定しました。");
    }

    // -------------------------------------------------------
    //  まとめて実行
    // -------------------------------------------------------

    [MenuItem("Tools/WebGL Optimize/Run All")]
    static void RunAll()
    {
        ApplyAudio();
        ApplyTextures();
        Debug.Log("[WebGL Optimize] 全最適化が完了しました。");
    }

    // -------------------------------------------------------
    //  ヘルパー
    // -------------------------------------------------------

    // フォルダ内の全テクスチャに WebGL プラットフォームオーバーライドを設定。
    // crunch=true で DXT Crunched 圧縮（ファイルサイズをさらに約50%削減）。
    static int ApplyTextureFolder(string folder, int maxSize, bool crunch)
    {
        int count = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
        {
            string path     = AssetDatabase.GUIDToAssetPath(guid);
            var    importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) continue;

            // Read/Write はオフが基本（変更されていれば強制オフ）
            if (importer.isReadable)
            {
                importer.isReadable = false;
            }

            var ps = new TextureImporterPlatformSettings
            {
                name                 = "WebGL",
                overridden           = true,
                maxTextureSize       = maxSize,
                format               = TextureImporterFormat.Automatic,
                textureCompression   = TextureImporterCompression.Compressed,
                compressionQuality   = 50,
                crunchedCompression  = crunch,
            };
            importer.SetPlatformTextureSettings(ps);
            importer.SaveAndReimport();
            count++;
        }
        return count;
    }
}
