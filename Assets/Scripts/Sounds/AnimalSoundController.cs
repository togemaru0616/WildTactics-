using UnityEngine;

// 各 AnimalUnit にアタッチされる音声コントローラー。
// AudioSource は AudioPool の共有ソースを使うため、このコンポーネント自体は
// AudioSource を保持しない（ユニット数に比例した AudioSource 増加を防ぐ）。
public class AnimalSoundController : MonoBehaviour
{
    AudioClip _attackClip;
    AudioClip _deathClip;
    float     _attackVolMult;

    public void Init(AudioClip attack, AudioClip death, float attackVolMult = 1f)
    {
        _attackClip    = attack;
        _deathClip     = death;
        _attackVolMult = attackVolMult;
    }

    public void PlayAttack() => AudioPool.PlayOneShot(_attackClip, _attackVolMult * SoundManager.AnimalVolume);
    public void PlayHit()    { }
    public void PlayDeath()  => AudioPool.PlayOneShot(_deathClip,  SoundManager.AnimalVolume);

    // polyperfect アニメーションクリップに埋め込まれた AnimationEvent の受け取り（no-op）
    void AnimalSound() { }
}
