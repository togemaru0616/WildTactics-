#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public static class CardViewTmpReplacer
{
    [MenuItem("WildTactics/Replace TMP→Text in Card Prefabs")]
    static void ReplaceAll()
    {
        var table = CardAssetTable.Instance;
        if (table == null) { Debug.LogError("[TmpReplacer] CardAssetTable not found"); return; }

        int fixed_ = 0;
        var prefabs = new System.Collections.Generic.List<GameObject>();
        if (table.cardPrefab != null) prefabs.Add(table.cardPrefab);
        foreach (var p in table.animalCardPrefabs)
            if (p != null) prefabs.Add(p);

        foreach (var prefab in prefabs)
        {
            string path = AssetDatabase.GetAssetPath(prefab);
            var root = PrefabUtility.LoadPrefabContents(path);

            var view = root.GetComponent<CardView>();
            if (view == null) { PrefabUtility.UnloadPrefabContents(root); continue; }

            var tmp = root.GetComponentInChildren<TextMeshProUGUI>(true);
            if (tmp == null) { PrefabUtility.UnloadPrefabContents(root); continue; }

            var go       = tmp.gameObject;
            string txt   = tmp.text;
            int fontSize = Mathf.RoundToInt(tmp.fontSize);
            var color    = tmp.color;
            var align    = tmp.alignment;

            Object.DestroyImmediate(tmp);

            var t          = go.AddComponent<Text>();
            t.font         = UIAssetTable.Instance?.font
                          ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text         = txt;
            t.fontSize     = fontSize > 0 ? fontSize : 18;
            t.fontStyle    = FontStyle.Bold;
            t.color        = color;
            t.alignment    = align == TextAlignmentOptions.Center ? TextAnchor.MiddleCenter
                           : align == TextAlignmentOptions.Left   ? TextAnchor.MiddleLeft
                           :                                        TextAnchor.MiddleRight;
            t.resizeTextForBestFit = false;

            view.nameText = t;

            PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);
            Debug.Log($"[TmpReplacer] 変換完了: {path}");
            fixed_++;
        }

        Debug.Log($"[TmpReplacer] {fixed_} 件のPrefabを変換しました");
        AssetDatabase.Refresh();
    }
}
#endif
