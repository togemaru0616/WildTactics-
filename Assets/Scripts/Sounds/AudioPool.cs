using UnityEngine;

// ユニット効果音用の共有 AudioSource プール。
// 従来はユニット 1 体ごとに AudioSource を追加していたが（10〜20 個）、
// ここに集約することで常時 AudioSource 数を固定数（LoopCount + OneShotCount）に抑える。
public static class AudioPool
{
    const int OneShotCount = 3;
    const int LoopCount    = 5;

    static AudioSource[] _oneShot;
    static AudioSource[] _loop;
    static int _oneShotIdx;

    static void EnsureInit()
    {
        // _oneShot[0] が null（初回 or シーン遷移後に破棄された）場合に再初期化
        if (_oneShot != null && _oneShot[0] != null) return;

        var go = new GameObject("[AudioPool]");
        // DontDestroyOnLoad は使用しない。
        // Editor の Play モード終了時に DontDestroyOnLoad オブジェクトが
        // GUIUtility.ProcessEvent と衝突してアサートを起こすため。
        // シーン遷移時は go が自動破棄され、次回 EnsureInit で再作成される。

        _oneShot = new AudioSource[OneShotCount];
        for (int i = 0; i < OneShotCount; i++)
            _oneShot[i] = MakeSrc(go);

        _loop = new AudioSource[LoopCount];
        for (int i = 0; i < LoopCount; i++)
            _loop[i] = MakeSrc(go);
    }

    static AudioSource MakeSrc(GameObject go)
    {
        var s = go.AddComponent<AudioSource>();
        s.playOnAwake  = false;
        s.spatialBlend = 0f;
        return s;
    }

    // 効果音（攻撃・死亡など）を鳴らす。PlayOneShot で重ね再生可能。
    public static void PlayOneShot(AudioClip clip, float volume)
    {
        if (clip == null) return;
        EnsureInit();

        // 再生中でないソースを優先して使う
        for (int i = 0; i < OneShotCount; i++)
        {
            if (!_oneShot[i].isPlaying)
            {
                _oneShot[i].PlayOneShot(clip, volume);
                return;
            }
        }
        // 全部使用中ならラウンドロビン
        _oneShot[_oneShotIdx % OneShotCount].PlayOneShot(clip, volume);
        _oneShotIdx++;
    }

    // ループ音（毒など）のソースを借りて再生開始。満杯なら null を返す。
    public static AudioSource BorrowLoop(AudioClip clip, float volume)
    {
        if (clip == null) return null;
        EnsureInit();
        foreach (var s in _loop)
        {
            if (s.isPlaying) continue;
            s.clip   = clip;
            s.volume = volume;
            s.loop   = true;
            s.Play();
            return s;
        }
        return null;
    }

    // ループ音のソースをプールに返す。
    public static void ReturnLoop(ref AudioSource src)
    {
        if (src == null) return;
        src.Stop();
        src.clip = null;
        src.loop = false;
        src = null;
    }
}
