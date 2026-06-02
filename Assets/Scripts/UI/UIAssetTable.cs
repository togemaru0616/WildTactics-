using UnityEngine;

[CreateAssetMenu(menuName = "WildTactics/UIAssetTable")]
public class UIAssetTable : ScriptableObject
{
    public Font font;

    [Header("Shaders")]
    [SerializeField] Shader _litShader;
    [SerializeField] Shader _unlitShader;

    public Shader litShader => _litShader != null ? _litShader : Shader.Find("Universal Render Pipeline/Lit");
    public Shader unlitShader => _unlitShader != null ? _unlitShader : Shader.Find("Universal Render Pipeline/Unlit");

#if UNITY_EDITOR
    public void SetShaders(Shader lit, Shader unlit) { _litShader = lit; _unlitShader = unlit; }
#endif

    [Header("Screens")]
    public Texture2D titleBackground;
    public Texture2D loadingScreen;
    public Texture2D victory;
    public Texture2D defeat;
    public Texture2D cooldownSpinner;

    static UIAssetTable _instance;
    public static UIAssetTable Instance
    {
        get
        {
            if (_instance == null)
                _instance = Resources.Load<UIAssetTable>("UIAssetTable");
            return _instance;
        }
    }
}
