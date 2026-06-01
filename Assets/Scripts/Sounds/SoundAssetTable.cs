using UnityEngine;

[CreateAssetMenu(menuName = "WildTactics/SoundAssetTable")]
public class SoundAssetTable : ScriptableObject
{
    [Header("BGM")]
    public AudioClip bgmTitle;
    public AudioClip bgmPlacement;
    public AudioClip bgmExplore;
    public AudioClip bgmCombat;
    public AudioClip bgmVictory;
    public AudioClip bgmDefeat;

    [Header("SE")]
    public AudioClip seTap;
    public AudioClip seCancel;
    public AudioClip seAnimalTouch;
    public AudioClip seHeal;
    public AudioClip sePoison;
    public AudioClip sePoisonBack;

    static SoundAssetTable _instance;
    public static SoundAssetTable Instance
    {
        get
        {
            if (_instance == null)
                _instance = Resources.Load<SoundAssetTable>("SoundAssetTable");
            return _instance;
        }
    }
}
