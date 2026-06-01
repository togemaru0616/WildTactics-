using UnityEngine;

public static class WebUtils
{
    // WebGL では Application.streamingAssetsPath が Unity6 で正しくない場合がある。
    // absoluteURL からゲームルートを導出して StreamingAssets パスを確実に返す。
    public static string StreamingAssetsUrl
    {
        get
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            string abs = Application.absoluteURL;
            int idx = abs.LastIndexOf('/');
            string baseDir = idx >= 0 ? abs.Substring(0, idx + 1) : abs;
            return baseDir + "StreamingAssets";
#else
            return Application.streamingAssetsPath;
#endif
        }
    }
}
