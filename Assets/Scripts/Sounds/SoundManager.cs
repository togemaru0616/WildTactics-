using UnityEngine;
using System.Collections;

public class SoundManager : MonoBehaviour
{
    public static SoundManager Instance { get; private set; }

    public static float BgmVolume    { get; private set; } = 1f;
    public static float SeVolume     { get; private set; } = 1f;
    public static float AnimalVolume { get; private set; } = 1f;

    const float BgmBase    = 0.22f;
    const float FadeOutDur = 0.8f;
    const float FadeInDur  = 1.0f;

    AudioSource _bgmA;
    AudioSource _bgmB;
    AudioSource _activeSrc;
    Coroutine   _fadeCo;

    AudioSource _seSrc;

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        BgmVolume    = PlayerPrefs.GetFloat("vol_bgm",    0.7f);
        SeVolume     = PlayerPrefs.GetFloat("vol_se",     0.7f);
        AnimalVolume = PlayerPrefs.GetFloat("vol_animal", 0.7f);

        _bgmA             = gameObject.AddComponent<AudioSource>();
        _bgmA.loop        = true;
        _bgmA.volume      = 0f;
        _bgmA.playOnAwake = false;

        _bgmB             = gameObject.AddComponent<AudioSource>();
        _bgmB.loop        = true;
        _bgmB.volume      = 0f;
        _bgmB.playOnAwake = false;

        _activeSrc = _bgmA;

        _seSrc              = gameObject.AddComponent<AudioSource>();
        _seSrc.loop         = false;
        _seSrc.volume       = SeVolume;
        _seSrc.playOnAwake  = false;

    }

    // ---- BGM ----


    public static void Title()     => Instance?.PlayBGM(SoundAssetTable.Instance.bgmTitle);
    public static void Placement() => Instance?.PlayBGM(SoundAssetTable.Instance.bgmPlacement);
    public static void Explore()   => Instance?.PlayBGM(SoundAssetTable.Instance.bgmExplore);
    public static void Combat()    => Instance?.PlayBGM(SoundAssetTable.Instance.bgmCombat);
    public static void Victory()   => Instance?.PlayBGM(SoundAssetTable.Instance.bgmVictory);
    public static void Defeat()    => Instance?.PlayBGM(SoundAssetTable.Instance.bgmDefeat);

    void PlayBGM(AudioClip clip)
    {
        if (clip == null) return;
        if (_activeSrc.clip == clip && _activeSrc.isPlaying) return;
        if (_fadeCo != null) StopCoroutine(_fadeCo);
        _fadeCo = StartCoroutine(WaitAndCrossFade(clip));
    }

    IEnumerator WaitAndCrossFade(AudioClip clip)
    {
        // WebGL では非同期ロードのためロード完了を待つ
        while (clip.loadState == AudioDataLoadState.Loading)
            yield return null;
        if (clip.loadState != AudioDataLoadState.Loaded)
        {
            clip.LoadAudioData();
            while (clip.loadState == AudioDataLoadState.Loading)
                yield return null;
        }
        if (clip.loadState == AudioDataLoadState.Loaded)
            yield return StartCoroutine(CrossFade(clip));
    }

    IEnumerator CrossFade(AudioClip nextClip)
    {
        var outSrc = _activeSrc;
        var inSrc  = outSrc == _bgmA ? _bgmB : _bgmA;
        _activeSrc = inSrc;

        float startVol = outSrc.isPlaying ? outSrc.volume : 0f;
        float fadeOut  = outSrc.isPlaying ? FadeOutDur : 0f;
        float duration = Mathf.Max(fadeOut, FadeInDur);

        inSrc.clip   = nextClip;
        inSrc.volume = 0f;
        inSrc.Play();

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float tOut = fadeOut > 0f ? Mathf.Clamp01(elapsed / fadeOut) : 1f;
            float tIn  = Mathf.Clamp01(elapsed / FadeInDur);

            outSrc.volume = Mathf.Lerp(startVol, 0f, tOut);
            inSrc.volume  = Mathf.Lerp(0f, BgmBase * BgmVolume, tIn);  // フェード中の音量変更にも追従

            yield return null;
        }

        outSrc.Stop();
        outSrc.clip   = null;
        inSrc.volume  = BgmBase * BgmVolume;
        _fadeCo = null;
    }

    // ---- SE ----

    public static void Tap()         => Instance?.PlaySE(SoundAssetTable.Instance.seTap);
    public static void Cancel()      => Instance?.PlaySE(SoundAssetTable.Instance.seCancel);
    public static void AnimalTouch() => Instance?.PlaySE(SoundAssetTable.Instance.seAnimalTouch);
    public static void Heal()        => Instance?.PlaySE(SoundAssetTable.Instance.seHeal, 0.25f);
    public static void Poison()      => Instance?.PlaySE(SoundAssetTable.Instance.sePoison);

    void PlaySE(AudioClip clip, float volumeScale = 1f)
    {
        if (clip == null) return;
        _seSrc.PlayOneShot(clip, volumeScale);
    }

    // ---- Volume ----

    public static void SetBgmVolume(float v)
    {
        BgmVolume = v;
        // フェード中は CrossFade が動的に BgmVolume を参照するので直接上書きしない
        if (Instance != null && Instance._fadeCo == null)
            Instance._activeSrc.volume = BgmBase * v;
        PlayerPrefs.SetFloat("vol_bgm", v);
        PlayerPrefs.Save();
    }

    public static void MuteBgm(bool mute)
    {
        if (Instance != null)
            Instance._activeSrc.volume = mute ? 0f : BgmBase * BgmVolume;
    }

    public static void SetSeVolume(float v)
    {
        SeVolume = v;
        if (Instance != null) Instance._seSrc.volume = v;
        PlayerPrefs.SetFloat("vol_se", v);
        PlayerPrefs.Save();
    }

    public static void SetAnimalVolume(float v)
    {
        AnimalVolume = v;
        PlayerPrefs.SetFloat("vol_animal", v);
        PlayerPrefs.Save();
    }
}
