using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using System.IO;

public static class BuildWebGL
{
    const string OutputPath = "WebGL-Build";

    // WebGL ビルド前の全設定チェック
    static void ApplyAllWebGLSettings()
    {
        AssignUIAssetTableShaders();
        ApplyWebGLAudioSettings();
        ApplyWebGLTextureSettings();
        Debug.Log("[BuildWebGL] WebGL設定の適用完了");
    }

    // UIAssetTable の litShader / unlitShader を確実に割り当てる
    // Shader.Find は WebGL では動作しないため、ビルド前にシリアライズ参照として保存する必要がある
    static void AssignUIAssetTableShaders()
    {
        var table = Resources.Load<UIAssetTable>("UIAssetTable");
        if (table == null) { Debug.LogError("[BuildWebGL] UIAssetTable not found"); return; }

        var lit   = Shader.Find("Universal Render Pipeline/Lit");
        var unlit = Shader.Find("Universal Render Pipeline/Unlit");

        if (lit == null || unlit == null)
        {
            Debug.LogError("[BuildWebGL] URP shaders not found. Check Package Manager.");
            return;
        }

        table.SetShaders(lit, unlit);
        EditorUtility.SetDirty(table);
        AssetDatabase.SaveAssets();
        Debug.Log("[BuildWebGL] UIAssetTable shaders assigned");
    }

    // テクスチャストリーミングを無効化（WebGL では不要かつ初回描画が遅くなる）
    static void ApplyWebGLTextureSettings()
    {
        var guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Resources" });
        int count = 0;
        foreach (var guid in guids)
        {
            var path     = AssetDatabase.GUIDToAssetPath(guid);
            var importer = AssetImporter.GetAtPath(path) as UnityEditor.TextureImporter;
            if (importer == null) continue;
            if (importer.streamingMipmaps)
            {
                importer.streamingMipmaps = false;
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                count++;
            }
        }
        if (count > 0)
            Debug.Log($"[BuildWebGL] テクスチャストリーミング無効化: {count}件");
    }

    // WebGL 向け音声設定を強制適用
    // Streaming はスレッド非対応の WebGL でクラッシュするため Compressed In Memory に統一
    static void ApplyWebGLAudioSettings()
    {
        var guids = AssetDatabase.FindAssets("t:AudioClip", new[] { "Assets/Resources/Sounds" });
        int count = 0;
        foreach (var guid in guids)
        {
            var path     = AssetDatabase.GUIDToAssetPath(guid);
            var importer = AssetImporter.GetAtPath(path) as AudioImporter;
            if (importer == null) continue;

            var settings = importer.defaultSampleSettings;
            bool changed = false;

            if (settings.loadType == AudioClipLoadType.Streaming)
            {
                settings.loadType = AudioClipLoadType.CompressedInMemory;
                changed           = true;
            }
            if (!settings.preloadAudioData)
            {
                settings.preloadAudioData = true;
                changed                   = true;
            }

            if (changed)
            {
                importer.defaultSampleSettings = settings;
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                count++;
            }
        }
        if (count > 0)
            Debug.Log($"[BuildWebGL] 音声設定を修正: {count}件");
    }

    [MenuItem("Build/Build WebGL")]
    static void Build()
    {
        ApplyAllWebGLSettings();
        // IL2CPP の並列ワーカーを 1 に制限して OOM を回避
        PlayerSettings.SetAdditionalIl2CppArgs("--jobs=1");
        var scenes = new[]
        {
            "Assets/Scenes/SampleScene.unity"
        };

        if (Directory.Exists(OutputPath))
            DeleteDirectory(OutputPath);
        Directory.CreateDirectory(OutputPath);

        var options = new BuildPlayerOptions
        {
            scenes       = scenes,
            locationPathName = OutputPath,
            target       = BuildTarget.WebGL,
            options      = BuildOptions.None,
        };

        var report = BuildPipeline.BuildPlayer(options);

        if (report.summary.result == BuildResult.Succeeded)
        {
            Debug.Log($"[BuildWebGL] 成功！ → {Path.GetFullPath(OutputPath)}");
            EditorUtility.RevealInFinder(OutputPath);
        }
        else
        {
            Debug.LogError($"[BuildWebGL] 失敗: {report.summary.totalErrors} エラー");
        }
    }

    // 読み取り専用ファイル（.git オブジェクト等）を含むフォルダも削除できる
    static void DeleteDirectory(string path)
    {
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }

    [MenuItem("Build/Build WebGL (Development)")]
    static void BuildDev()
    {
        ApplyAllWebGLSettings();
        var scenes = new[]
        {
            "Assets/Scenes/SampleScene.unity"
        };

        const string devPath = "WebGL-Build-Dev";
        if (Directory.Exists(devPath))
            DeleteDirectory(devPath);
        Directory.CreateDirectory(devPath);

        var options = new BuildPlayerOptions
        {
            scenes           = scenes,
            locationPathName = devPath,
            target           = BuildTarget.WebGL,
            options          = BuildOptions.Development | BuildOptions.AllowDebugging,
        };

        var report = BuildPipeline.BuildPlayer(options);

        if (report.summary.result == BuildResult.Succeeded)
        {
            Debug.Log($"[BuildWebGL] Dev成功！ → {Path.GetFullPath(devPath)}");
            EditorUtility.RevealInFinder(devPath);
        }
        else
        {
            Debug.LogError($"[BuildWebGL] Dev失敗: {report.summary.totalErrors} エラー");
        }
    }
}
