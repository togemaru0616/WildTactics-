using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;

public static class WebGLPostBuild
{
    [PostProcessBuild]
    public static void OnPostProcessBuild(BuildTarget target, string path)
    {
        if (target != BuildTarget.WebGL) return;

        var buildDir = Path.Combine(path, "Build");
        foreach (var file in Directory.GetFiles(buildDir, "*.symbols.json"))
        {
            File.Delete(file);
            UnityEngine.Debug.Log($"[WebGLPostBuild] Deleted: {Path.GetFileName(file)}");
        }
    }
}
