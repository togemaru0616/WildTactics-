using UnityEditor;
using UnityEngine;

public static class TextureSizeOptimizer
{
    [MenuItem("Tools/Optimize Texture Sizes")]
    static void Run()
    {
        int count = 0;

        // 全画面画像（VICTORY/DEFEAT/Loading/Title）: 高品質を保持
        count += Apply(
            new[] { "Assets/Resources/UI" },
            maxSize: 1024,
            format: TextureImporterFormat.DXT5,
            quality: 100,
            // UI/Cards と UI/Terrain は後のルールで上書きするので除外
            excludeSubfolders: new[] { "Assets/Resources/UI/Cards", "Assets/Resources/UI/Terrain" }
        );

        // カードアート（動物イラスト）
        count += Apply(
            new[] { "Assets/Resources/Cards" },
            maxSize: 256,
            format: TextureImporterFormat.DXT5Crunched,
            quality: 50
        );

        // 地形タイル（ほぼ不透明）
        count += Apply(
            new[] { "Assets/Resources/Tiles" },
            maxSize: 256,
            format: TextureImporterFormat.DXT1Crunched,
            quality: 50
        );

        // 小UIアイコン（カードフレーム・ランク・テレインアイコン）
        count += Apply(
            new[] { "Assets/Resources/UI/Cards", "Assets/Resources/UI/Terrain" },
            maxSize: 256,
            format: TextureImporterFormat.DXT5Crunched,
            quality: 50
        );

        // 動物 3D テクスチャ
        count += Apply(
            new[] { "Assets/ThirdParty/PolyPerfect/Low Poly Animated Animals/Textures/Animals" },
            maxSize: 512,
            format: TextureImporterFormat.DXT5Crunched,
            quality: 50
        );

        Debug.Log($"[TextureSizeOptimizer] {count} 枚のテクスチャを最適化しました");
        EditorUtility.DisplayDialog("完了", $"{count} 枚を最適化しました", "OK");
    }

    static int Apply(string[] folders, int maxSize, TextureImporterFormat format, int quality,
                     string[] excludeSubfolders = null)
    {
        int count = 0;
        var guids = AssetDatabase.FindAssets("t:Texture2D", folders);
        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);

            if (excludeSubfolders != null)
            {
                bool skip = false;
                foreach (var ex in excludeSubfolders)
                    if (path.StartsWith(ex)) { skip = true; break; }
                if (skip) continue;
            }

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) continue;

            bool changed = false;

            if (importer.maxTextureSize > maxSize)
            {
                importer.maxTextureSize = maxSize;
                changed = true;
            }

            var webgl = importer.GetPlatformTextureSettings("WebGL");
            if (!webgl.overridden || webgl.maxTextureSize > maxSize ||
                webgl.format != format || webgl.compressionQuality != quality)
            {
                webgl.overridden         = true;
                webgl.maxTextureSize     = maxSize;
                webgl.format             = format;
                webgl.compressionQuality = quality;
                importer.SetPlatformTextureSettings(webgl);
                changed = true;
            }

            if (!changed) continue;
            importer.SaveAndReimport();
            count++;
        }
        return count;
    }
}
