using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// ビルド前に persistentDataPath/best_weights.json を
/// Assets/Resources/best_weights.json へ自動コピーする。
/// WebGL等ファイルシステム非対応環境でも学習済み重みが使われる。
public class WeightsBuildPreprocessor : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;

    const string SRC_FILENAME  = "best_weights.json";
    const string DEST_ASSET    = "Assets/Resources/best_weights.json";

    public void OnPreprocessBuild(BuildReport report)
    {
        string src = Path.Combine(Application.persistentDataPath, SRC_FILENAME);
        if (!File.Exists(src))
        {
            Debug.LogWarning($"[WeightsBuild] 学習済み重みが見つかりません: {src}\nデフォルト重みでビルドします。");
            return;
        }

        string dest = Path.Combine(Application.dataPath, "Resources", SRC_FILENAME);
        File.Copy(src, dest, overwrite: true);
        AssetDatabase.ImportAsset(DEST_ASSET, ImportAssetOptions.ForceUpdate);
        Debug.Log($"[WeightsBuild] 学習済み重みをコピーしました → {DEST_ASSET}");
    }
}
