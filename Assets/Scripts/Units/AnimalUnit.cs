using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class AnimalUnit : MonoBehaviour
{
    // Set to true before spawning units to skip all animations and cooldowns
    public static bool SimMode = false;

    public AnimalType AnimalType = AnimalType.Fox;
    public int        Owner      = 1;

    public int        MaxHP       { get; private set; }
    public int        CurrentHP   { get; private set; }
    public int        AttackPower { get; private set; }
    public Vector2Int GridPos     { get; internal set; }
    public bool       IsMoving         { get; private set; }
    public bool       IsDead           { get; private set; }
    public bool       IsActing         => IsMoving || _isAttacking;
    public bool       IsOnMoveCooldown => _moveCooldown > 0f;
    public bool       CanMove          => !IsDead && !IsActing && (SimMode || (GameManager.Instance != null && GameManager.Instance.Phase == GamePhase.Battle)) && _moveCooldown <= 0f;
    public bool       CanAttack        => !IsDead && !IsMoving && !_isAttacking && _attackCooldown <= 0f && (SimMode || (GameManager.Instance != null && GameManager.Instance.Phase == GamePhase.Battle));
    public bool       CanAct           => CanMove;

    public int NetworkId { get; set; }

    bool  _isAttacking;
    float _moveCooldown;
    float _moveCooldownMax;
    float _attackCooldown;
    bool  _isVisible = true;
    public bool IsVisible => _isVisible;

    // 毒
    int        _poisonedTurns;
    public int PoisonedTurns => _poisonedTurns;
    AnimalUnit _poisonApplier;
    Coroutine  _poisonCo;
    GameObject _poisonIndicator;
    AudioSource _poisonLoopSrc;

    // キャンプ回復
    const float CampHealInterval = 2f;
    const int   CampHealAmount   = 1;
    Coroutine   _campHealCo;

    // クールダウンインジケーター
    GameObject _cdIndicator;
    bool _cdIndicatorVisible;

    // ターゲット管理
    AnimalUnit  _lockedTarget;       // プレイヤーが手動で選んだターゲット
    AnimalUnit  _lastTarget;         // 前回攻撃したターゲット（継続攻撃用）
    AnimalUnit  _pendingPushTarget;  // 突進アニメ完了時に吹き飛ばす対象
    Vector2Int? _pendingMoveTarget;  // 攻撃中に受け付けた移動コマンド（攻撃完了後に実行）

    float _attackAnimDur = 0.8f;   // 攻撃クリップ実長（Awakeでキャッシュ）

    // 毎フレーム new しないための再利用バッファ・待機オブジェクト
    readonly List<AnimalUnit> _enemiesBuffer = new(8);
    static readonly WaitForSeconds _waitAttackTick = new(0.1f);

    // コルーチン参照（Die時に個別停止するため）
    Coroutine _autoAttackCo;
    Coroutine _moveCo;

    Animator              _animator;
    AnimalSoundController _sound;

    // HP バー
    Transform  _hpBarFill;
    Material   _hpBarMat;
    GameObject _hpBarRoot;
    Coroutine  _hpHideCo;
    const float HpBarDuration = 3f;

    // ---- 初期化 ----

    void Awake()
    {
        MaxHP       = AnimalDefinitions.GetMaxHP(AnimalType);
        AttackPower = AnimalDefinitions.GetAttackPower(AnimalType);
        CurrentHP   = MaxHP;
        _animator   = GetComponentInChildren<Animator>();

        CacheAnimDurations();

        if (!SimMode)
        {
            // プレハブ由来の残存 AudioSource を停止（SpawnUnit で除去済みだが念のため）
            foreach (var src in GetComponentsInChildren<AudioSource>(true))
            { src.Stop(); src.playOnAwake = false; }

            // 前回の参照をクリアしてから新規追加
            _sound = null;
            _sound = gameObject.AddComponent<AnimalSoundController>();
            // AnimatorはモデルのchildにあるためAnimationEventの受け取りには同一GOへのアタッチが必要
            if (_animator != null && _animator.gameObject != gameObject)
                _animator.gameObject.AddComponent<AnimalSoundController>();
            var entry = AnimalAssetTable.Instance?.Get(AnimalType);
            _sound.Init(entry?.attackClip, entry?.deathClip, entry?.attackVolumeMult ?? 1f);

            CreateHPBar();
            CreateCooldownIndicator();
            CreatePoisonIndicator();
        }
    }

    void Start()
    {
        SetAnim(AnimState.Idle);
        _autoAttackCo = StartCoroutine(AutoAttackLoop());
    }

    void Update()
    {
        if (!IsDead)
        {
            if (_moveCooldown   > 0f) _moveCooldown   -= Time.deltaTime;
            if (_attackCooldown > 0f) _attackCooldown -= Time.deltaTime;
        }
        UpdateCooldownIndicator();
    }

    // ---- 配置 ----

    public void PlaceAt(Vector2Int pos)
    {
        GridPos = pos;
        SyncWorldPos();
    }

    // ---- 移動（1 マス直行・経路探索なし）----

    public void MoveTo(Vector2Int dest)
    {
        if (!CanMove) return;
        _lockedTarget = null;

        // 突進2マス以外は移動先が既に占有されていたらキャンセル
        var  offChk  = dest - GridPos;
        bool is2chk  = Mathf.Abs(offChk.x) == 2 || Mathf.Abs(offChk.y) == 2;
        bool isChargePush = AnimalDefinitions.HasCharge(AnimalType) && is2chk;
        if (!isChargePush)
        {
            var preOcc = UnitManager.Instance.GetUnitAt(dest);
            if (preOcc != null && !preOcc.IsDead) return;
        }

        // 突進: 2マス移動先に敵がいる場合、押し出してから進む
        if (AnimalDefinitions.HasCharge(AnimalType))
        {
            var  off    = dest - GridPos;
            bool is2    = Mathf.Abs(off.x) == 2 || Mathf.Abs(off.y) == 2;
            if (is2)
            {
                var enemy = UnitManager.Instance.GetUnitAt(dest);
                if (enemy != null && enemy.Owner != Owner && !enemy.IsDead)
                {
                    var pushDir  = new Vector2Int(off.x > 0 ? 1 : off.x < 0 ? -1 : 0,
                                                   off.y > 0 ? 1 : off.y < 0 ? -1 : 0);
                    var pushDest = dest + pushDir;
                    if (!CanPush(enemy, pushDest)) return;
                    if (SimMode)
                    {
                        // SimMode: 即時処理
                        enemy.PushTo(pushDest);
                        enemy.TakeDamage(AnimalDefinitions.GetChargeDamage(AnimalType), this);
                    }
                    else
                    {
                        // 通常プレイ: 論理位置を即予約、ビジュアルはアニメ完了後
                        enemy.ReservePush(pushDest);
                        _pendingPushTarget = enemy;
                    }
                }
            }
        }

        _moveCo = StartCoroutine(MoveCoroutine(dest));
    }

    // プレイヤー入力用: 攻撃中ならキューして攻撃完了後に移動。true=受付成功（即実行 or キュー）
    public bool RequestMoveTo(Vector2Int dest)
    {
        if (IsDead || IsMoving) return false;
        if (IsOnMoveCooldown) return false;

        if (_isAttacking)
        {
            _pendingMoveTarget = dest;
            return true;
        }

        if (!CanMove) return false;
        MoveTo(dest);
        return true;
    }

    // 論理位置のみ即時予約（ビジュアルは突進アニメ完了後に StartPushAnim で開始）
    public void ReservePush(Vector2Int dest)
    {
        if (IsDead) return;
        UnitManager.Instance.NotifyMoved(this, GridPos, dest);
        GridPos = dest;
    }

    // 突進ユニットのアニメ完了時に呼ばれる（ビジュアル移動を開始）
    public Coroutine StartPushAnim()
    {
        if (IsDead) return null;
        return StartCoroutine(PushCoroutine(GridPos));
    }

    // 対象ユニットを強制的に1マス押し出す（SimMode / SimBoard 専用）
    public void PushTo(Vector2Int dest)
    {
        if (IsDead) return;
        UnitManager.Instance.NotifyMoved(this, GridPos, dest);
        GridPos = dest;
        if (SimMode) { SyncWorldPos(); return; }
        StartCoroutine(PushCoroutine(dest));
    }

    IEnumerator PushCoroutine(Vector2Int dest)
    {
        Vector3 from = transform.position;
        Vector3 to   = GridManager.Instance.TileToWorld(dest.x, dest.y) + TileOffset();
        float   t    = 0f;
        while (t < 1f)
        {
            t += Time.deltaTime / 0.25f;
            transform.position = Vector3.Lerp(from, to, Mathf.Min(t, 1f));
            yield return null;
        }
        transform.position = to;
    }

    bool CanPush(AnimalUnit enemy, Vector2Int pushDest)
    {
        if (enemy.AnimalType == AnimalType.Elephant) return false;
        var tile = GridManager.Instance.GetTile(pushDest.x, pushDest.y);
        if (tile == null) return false;
        var blocked = AnimalDefinitions.GetBlockedTerrain(enemy.AnimalType);
        if (tile.Type != TerrainType.Bridge && blocked.Contains(tile.Type)) return false;
        var occ = UnitManager.Instance.GetUnitAt(pushDest);
        return occ == null || occ.IsDead;
    }

    IEnumerator MoveCoroutine(Vector2Int dest)
    {
        IsMoving = true;
        StopCampHeal();

        var   fromPos = GridPos;
        var   tile    = GridManager.Instance.GetTile(dest.x, dest.y);
        float mult    = tile != null ? AnimalDefinitions.GetTerrainMult(AnimalType, tile.Type) : 1f;
        int   dist    = Mathf.Max(Mathf.Abs(dest.x - fromPos.x), Mathf.Abs(dest.y - fromPos.y));
        float moveDur = AnimalDefinitions.GetMoveTime(AnimalType) * mult * (dist >= 2 ? 0.85f : 1f);

        // 移動開始と同時に論理位置を確定（他ユニットが目的地に侵入するのを防ぐ）
        UnitManager.Instance.NotifyMoved(this, fromPos, dest);
        GridPos = dest;

        // クールダウンをアニメーション開始時にセット
        // → アニメと並行してカウントダウンされ、移動完了時に自動的に満了する
        _moveCooldownMax = moveDur;
        _moveCooldown    = moveDur;
        if (OutpostManager.Instance != null
            && OutpostManager.Instance.IsOutpost(GridPos)
            && OutpostManager.Instance.GetOwner(GridPos) == Owner)
            _moveCooldown = 0f;

        if (SimMode)
        {
            SyncWorldPos();
            IsMoving        = false;
            _attackCooldown = moveDur * 0.67f;
            yield break;
        }

        SetAnim(dist >= 2 ? AnimState.Run : AnimState.Walk);
        FaceToward(GridManager.Instance.TileToWorld(dest.x, dest.y));

        Vector3 from = transform.position;
        Vector3 to   = GridManager.Instance.TileToWorld(dest.x, dest.y) + TileOffset();

        float t = 0f;
        while (t < 1f)
        {
            if (IsDead) yield break;
            t += Time.deltaTime / moveDur;
            transform.position = Vector3.Lerp(from, to, Mathf.Min(t, 1f));
            yield return null;
        }
        transform.position = to;
        SetAnim(AnimState.Idle);
        RestoreDefaultFacing();

        // 突進: 到達と同時にダメージ＋吹き飛ばしアニメ開始、完了まで IsMoving を維持
        var pushTarget = _pendingPushTarget;
        _pendingPushTarget = null;
        if (pushTarget != null && !pushTarget.IsDead)
        {
            // オンライン: 所有者のマシンだけダメージを計算して送信
            if (!OnlineManager.IsOnline || Owner == OnlineManager.LocalPlayer)
            {
                int chargeDmg = AnimalDefinitions.GetChargeDamage(AnimalType);
                pushTarget.TakeDamage(chargeDmg, this);
                if (OnlineManager.IsOnline)
                    OnlineGame.Instance?.SendDamage(pushTarget.NetworkId, chargeDmg, NetworkId);
            }
            var pushCo = pushTarget.StartPushAnim();
            if (pushCo != null) yield return pushCo;
        }

        IsMoving        = false;
        _attackCooldown = moveDur * 0.67f;
        if (IsCampTile(GridPos))
            _campHealCo = StartCoroutine(CampHealCoroutine());
    }

    // ---- 自動攻撃ループ ----

    IEnumerator AutoAttackLoop()
    {
        while (!IsDead)
        {
            // オンライン: 自分が所有するユニットだけ攻撃を実行
            if (OnlineManager.IsOnline && Owner != OnlineManager.LocalPlayer)
            {
                yield return new WaitForSeconds(0.5f);
                continue;
            }

            while (!CanAttack) yield return _waitAttackTick;

            if (!CanAttack) { yield return null; continue; }

            var enemies = GetEnemiesInRange();
            if (enemies.Count == 0) { yield return null; continue; }

            var target = PickTarget(enemies);
            _lastTarget = target;
            yield return StartCoroutine(AttackCoroutine(target));
        }
    }

    // ターゲット優先順位:
    //  1. プレイヤーがロックしたターゲット（まだ範囲内）
    //  2. 前回攻撃していたターゲット（継続攻撃）
    //  3. HP が最小の敵（新規）
    AnimalUnit PickTarget(List<AnimalUnit> enemies)
    {
        if (_lockedTarget != null && !_lockedTarget.IsDead && enemies.Contains(_lockedTarget))
            return _lockedTarget;
        if (_lastTarget != null && !_lastTarget.IsDead && enemies.Contains(_lastTarget))
            return _lastTarget;

        var best = enemies[0];
        foreach (var e in enemies)
            if (e.CurrentHP < best.CurrentHP) best = e;
        return best;
    }

    // 攻撃範囲内の敵一覧（MoveOffsets + AttackOnlyOffsets で判定）
    // 戻り値は内部バッファの参照 ── 呼び出し元は即時参照のみ、保存禁止
    public List<AnimalUnit> GetEnemiesInRange()
    {
        var offsets   = AnimalDefinitions.GetMoveOffsets(AnimalType);
        var blocked   = AnimalDefinitions.GetBlockedTerrain(AnimalType);
        int frontSign = Owner == 1 ? 1 : -1;
        _enemiesBuffer.Clear();

        foreach (var raw in offsets)
        {
            var off  = new Vector2Int(raw.x, raw.y * frontSign);
            var pos  = GridPos + off;
            if (pos.x < 0 || pos.x >= TerrainGenerator.COLS) continue;
            if (pos.y < 0 || pos.y >= TerrainGenerator.ROWS) continue;
            var tile = GridManager.Instance.GetTile(pos.x, pos.y);
            if (tile == null) continue;

            if (Mathf.Abs(off.x) == 2 || Mathf.Abs(off.y) == 2)
            {
                var mid = GridPos + new Vector2Int(off.x / 2, off.y / 2);
                var mt  = GridManager.Instance.GetTile(mid.x, mid.y);
                if (mt == null || (mt.Type != TerrainType.Bridge && blocked.Contains(mt.Type))) continue;
            }

            var unit = UnitManager.Instance.GetUnitAt(pos);
            if (unit != null && unit.Owner != Owner && !unit.IsDead)
                _enemiesBuffer.Add(unit);
        }

        // 攻撃専用オフセット（地形制限なし・遠距離攻撃）
        foreach (var raw in AnimalDefinitions.GetAttackOnlyOffsets(AnimalType))
        {
            var pos = GridPos + new Vector2Int(raw.x, raw.y * frontSign);
            if (GridManager.Instance.GetTile(pos.x, pos.y) == null) continue;
            var unit = UnitManager.Instance.GetUnitAt(pos);
            if (unit != null && unit.Owner != Owner && !unit.IsDead)
                _enemiesBuffer.Add(unit);
        }

        return _enemiesBuffer;
    }

    // プレイヤーが手動でターゲットを指定
    public void LockTarget(AnimalUnit target)
    {
        _lockedTarget = target;
        _lastTarget   = target;
    }

    // ---- 攻撃コルーチン ----

    IEnumerator AttackCoroutine(AnimalUnit target)
    {
        _isAttacking = true;

        if (SimMode)
        {
            if (!IsDead && target != null && !target.IsDead)
            {
                target.TakeDamage(AttackPower, this);
                if (AnimalType == AnimalType.Snake && !target.IsDead)
                    target.ApplyPoison(AnimalDefinitions.SnakePoisonDuration, this);
            }
            _isAttacking    = false;
            _attackCooldown = 2f;
            yield break;
        }

        FaceToward(target.transform.position);
        SetAnim(AnimState.Attack);
        _sound?.PlayAttack();

        float animDur = _attackAnimDur;
        yield return new WaitForSeconds(animDur * 0.4f);

        if (!IsDead && target != null && !target.IsDead)
        {
            target.TakeDamage(AttackPower, this);
            if (OnlineManager.IsOnline)
            {
                OnlineGame.Instance?.SendDamage(target.NetworkId, AttackPower, NetworkId);
                if (AnimalType == AnimalType.Snake && !target.IsDead)
                    OnlineGame.Instance?.SendPoison(target.NetworkId, AnimalDefinitions.SnakePoisonDuration);
            }
            if (AnimalType == AnimalType.Snake && !target.IsDead)
                target.ApplyPoison(AnimalDefinitions.SnakePoisonDuration);
        }

        yield return new WaitForSeconds(animDur * 0.6f);

        if (!IsDead) { SetAnim(AnimState.Idle); RestoreDefaultFacing(); }
        _isAttacking    = false;
        _attackCooldown = 2f;

        // キューされた移動コマンドを実行（攻撃完了後なので CanMove が true になっている前提）
        if (_pendingMoveTarget.HasValue)
        {
            var queued = _pendingMoveTarget.Value;
            _pendingMoveTarget = null;
            MoveTo(queued);
        }
    }

    // オンライン: 相手端末の攻撃アニメーションとダメージをローカルと同じタイミングで処理
    public void PlayRemoteAttack(AnimalUnit target, int damage)
    {
        if (SimMode || IsDead || target == null) return;
        StartCoroutine(RemoteAttackAnim(target, damage));
    }

    IEnumerator RemoteAttackAnim(AnimalUnit target, int damage)
    {
        _isAttacking = true;
        FaceToward(target.transform.position);
        SetAnim(AnimState.Attack);
        _sound?.PlayAttack();
        yield return new WaitForSeconds(_attackAnimDur * 0.4f);
        if (!IsDead && target != null && !target.IsDead)
            target.TakeDamage(damage, this);
        yield return new WaitForSeconds(_attackAnimDur * 0.6f);
        if (!IsDead) { SetAnim(AnimState.Idle); RestoreDefaultFacing(); }
        _isAttacking = false;
    }

    // ---- ダメージ・死亡 ----

    public void ApplyStatMultiplier(float mult)
    {
        MaxHP       = Mathf.RoundToInt(MaxHP       * mult);
        AttackPower = Mathf.RoundToInt(AttackPower * mult);
        CurrentHP   = MaxHP;
        UpdateHPBar();
    }

    public void Heal(int amount)
    {
        if (IsDead) return;
        if (CurrentHP >= MaxHP) return;
        SoundManager.Heal();
        CurrentHP = Mathf.Min(MaxHP, CurrentHP + amount);
        UpdateHPBar();
        ShowHPBar();
    }

    public void TakeDamage(int damage, AnimalUnit attacker)
    {
        if (IsDead) return;
        CurrentHP = Mathf.Max(0, CurrentHP - damage);
        UpdateHPBar();
        ShowHPBar();
        _sound?.PlayHit();
        if (!_isAttacking && !IsMoving && !SimMode) StartCoroutine(PlayHurtAnim());
        if (OutpostManager.Instance != null) OutpostManager.Instance.NotifyUnitDamaged(GridPos);
        if (CurrentHP <= 0) Die(attacker);
    }

    void Die(AnimalUnit killer = null)
    {
        if (IsDead) return;
        IsDead = true;

        // まずコルーチンを全停止（以降の処理に干渉させない）
        StopCo(ref _autoAttackCo);
        StopCo(ref _moveCo);
        StopCo(ref _poisonCo);
        StopCo(ref _hpHideCo);
        StopCampHeal();

        // 状態クリア
        _isAttacking       = false;
        IsMoving           = false;
        _poisonedTurns     = 0;
        _pendingMoveTarget = null;
        _pendingPushTarget = null;

        // インジケーター非表示
        if (_poisonIndicator != null) _poisonIndicator.SetActive(false);
        if (_hpBarRoot       != null) _hpBarRoot.SetActive(false);
        StopPoisonLoop();
        if (_cdIndicator != null) Destroy(_cdIndicator);

        // グリッドから除去（先にやることで他ユニットが空きと誤認しない）
        UnitManager.Instance.RemoveUnit(this);

        if (AnimalType == AnimalType.Fox && GameManager.Instance != null)
        {
            AnimalType killerType = killer != null ? killer.AnimalType : AnimalType.Fox;
            GameManager.Instance.NotifyFoxDeath(Owner, killerType, transform.position);
        }

        SetAnim(AnimState.Death);
        _sound?.PlayDeath();
        Destroy(gameObject, 2f);
    }

    public void ForceKill() => Die();

    public void OnTapped() { }

    // ---- アニメーション ----

    enum AnimState { Idle, Walk, Run, Attack, Death, Hurt }

    void SetAnim(AnimState s)
    {
        if (_animator == null) return;
        switch (s)
        {
            case AnimState.Idle:
                ResetStateBools();
                CrossFadeState("Idle", "idle");
                break;
            case AnimState.Walk:
                ResetStateBools();
                TrySetBool("isWalking", true);
                CrossFadeState("Walk", "walk", "Slither"); // Snake: Slither
                break;
            case AnimState.Run:
                ResetStateBools();
                TrySetBool("isRunning", true);
                CrossFadeState("Run", "run");
                break;
            case AnimState.Attack:
                ResetStateBools();
                CrossFadeState("Attack", "attack");
                break;
            case AnimState.Death:
                ResetStateBools();
                CrossFadeState("Death", "dead");
                break;
            case AnimState.Hurt:
                TrySetBool("isChestHit", true);
                break;
        }
    }

    void ResetStateBools()
    {
        TrySetBool("isWalking",  false);
        TrySetBool("isRunning",  false);
        TrySetBool("isChestHit", false);
    }

    // CrossFade で即座にステートに入る（HasExitTime をバイパス）
    // Bool は別途 TrySetBool で true にしてステートをキープする
    void CrossFadeState(params string[] names)
    {
        foreach (var name in names)
        {
            var h = Animator.StringToHash(name);
            if (_animator.HasState(0, h)) { _animator.CrossFade(h, 0.1f); return; }
        }
    }

    bool TrySetBool(string n, bool value)
    {
        foreach (var p in _animator.parameters)
            if (p.name == n && p.type == AnimatorControllerParameterType.Bool)
            { _animator.SetBool(n, value); return true; }
        return false;
    }

    IEnumerator PlayHurtAnim()
    {
        SetAnim(AnimState.Hurt);
        yield return new WaitForSeconds(0.7f);
        if (!IsDead && !_isAttacking && !IsMoving) SetAnim(AnimState.Idle);
    }

    void CacheAnimDurations()
    {
        if (_animator == null) return;
        var ctrl = _animator.runtimeAnimatorController;
        if (ctrl == null) return;
        foreach (var clip in ctrl.animationClips)
        {
            string n = clip.name.ToLower();
            if (n.Contains("attack") || n.Contains("atk") || n.Contains("bite") || n.Contains("scratch"))
                _attackAnimDur = clip.length;
        }
    }

    // ---- 向き ----

    void FaceToward(Vector3 worldTarget)
    {
        var dir = worldTarget - transform.position; dir.y = 0f;
        if (dir.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(dir);
    }

    void RestoreDefaultFacing()
        => transform.rotation = Quaternion.Euler(0f, Owner == 1 ? 0f : 180f, 0f);

    // ---- HP バー ----

    void CreateHPBar()
    {
        float w = GridManager.Instance != null ? GridManager.Instance.TileSize * 0.85f : 0.85f;
        _hpBarRoot = new GameObject("HPBar");
        _hpBarRoot.transform.SetParent(transform);
        _hpBarRoot.transform.localPosition = Vector3.zero;

        var bg = GameObject.CreatePrimitive(PrimitiveType.Cube);
        bg.name = "HPBar_BG";
        bg.transform.SetParent(_hpBarRoot.transform);
        bg.transform.localPosition = new Vector3(0f, 0.35f, 0.3f);
        bg.transform.localScale    = new Vector3(w, 0.04f, 0.08f);
        Destroy(bg.GetComponent<Collider>());
        var bgMat = new Material(UIAssetTable.Instance.unlitShader);
        bgMat.color = new Color(0.25f, 0.05f, 0.05f);
        bg.GetComponent<MeshRenderer>().material = bgMat;

        var fill = GameObject.CreatePrimitive(PrimitiveType.Cube);
        fill.name = "HPBar_Fill";
        fill.transform.SetParent(_hpBarRoot.transform);
        fill.transform.localPosition = new Vector3(0f, 0.36f, 0.3f);
        fill.transform.localScale    = new Vector3(w, 0.04f, 0.08f);
        Destroy(fill.GetComponent<Collider>());
        _hpBarMat = new Material(UIAssetTable.Instance.unlitShader);
        _hpBarMat.color = new Color(0.15f, 0.85f, 0.15f);
        fill.GetComponent<MeshRenderer>().material = _hpBarMat;
        _hpBarFill = fill.transform;
        _hpBarRoot.SetActive(false);
    }

    void UpdateHPBar()
    {
        if (_hpBarFill == null) return;
        float ratio = (float)CurrentHP / MaxHP;
        float w     = GridManager.Instance != null ? GridManager.Instance.TileSize * 0.85f : 0.85f;
        _hpBarFill.localScale    = new Vector3(w * ratio, 0.04f, 0.08f);
        _hpBarFill.localPosition = new Vector3(-w * (1f - ratio) * 0.5f, 0.36f, 0.3f);
        _hpBarMat.color = Color.Lerp(new Color(0.9f, 0.1f, 0.1f), new Color(0.15f, 0.85f, 0.15f), ratio);
    }

    void ShowHPBar()
    {
        if (_hpBarRoot == null) return;
        _hpBarRoot.SetActive(true);
        if (_hpHideCo != null) StopCoroutine(_hpHideCo);
        _hpHideCo = StartCoroutine(HideHPBarDelay());
    }

    IEnumerator HideHPBarDelay()
    {
        yield return new WaitForSeconds(HpBarDuration);
        if (_hpBarRoot != null) _hpBarRoot.SetActive(false);
    }

    // ---- クールダウンインジケーター ----

    void CreateCooldownIndicator()
    {
        _cdIndicator = GameObject.CreatePrimitive(PrimitiveType.Quad);
        _cdIndicator.name = "CooldownIndicator";
        Destroy(_cdIndicator.GetComponent<MeshCollider>());
        _cdIndicator.transform.localScale = Vector3.one * 0.2f;

        var tex = UIAssetTable.Instance.cooldownSpinner;
        var mat = new Material(UIAssetTable.Instance.unlitShader);
        mat.SetTexture("_BaseMap", tex);
        mat.SetColor("_BaseColor", Color.white);
        mat.SetFloat("_Surface", 1f);
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite",   0);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = 3003;
        _cdIndicator.GetComponent<MeshRenderer>().material = mat;
        _cdIndicator.SetActive(false);
    }

    void UpdateCooldownIndicator()
    {
        if (_cdIndicator == null) return;

        bool show = _isVisible && _moveCooldown > 0f && !IsDead;
        if (show != _cdIndicatorVisible)
        {
            _cdIndicatorVisible = show;
            _cdIndicator.SetActive(show);
        }
        if (!show) return;

        _cdIndicator.transform.position = transform.position + new Vector3(0f, -0.1f, 0f);
        float ratio = _moveCooldownMax > 0f ? 1f - (_moveCooldown / _moveCooldownMax) : 0f;
        _cdIndicator.transform.rotation = Quaternion.Euler(-90f, 0f, ratio * 360f);
    }

    // ---- 毒インジケーター ----

    void CreatePoisonIndicator()
    {
        _poisonIndicator = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _poisonIndicator.name = "PoisonIndicator";
        _poisonIndicator.transform.SetParent(transform);
        _poisonIndicator.transform.localPosition = new Vector3(0.38f, 0.55f, 0.3f);
        _poisonIndicator.transform.localScale    = new Vector3(0.13f, 0.13f, 0.13f);
        Destroy(_poisonIndicator.GetComponent<Collider>());
        var mat = new Material(UIAssetTable.Instance.unlitShader);
        mat.color        = new Color(0.35f, 0.95f, 0.10f, 0.92f);
        mat.renderQueue  = 3002;
        _poisonIndicator.GetComponent<MeshRenderer>().material = mat;
        _poisonIndicator.SetActive(false);
    }

    // ---- キャンプ回復 ----

    void StopCo(ref Coroutine co)
    {
        if (co != null) { StopCoroutine(co); co = null; }
    }

    void StopCampHeal()
    {
        StopCo(ref _campHealCo);
    }

    static bool IsCampTile(Vector2Int pos)
    {
        var tile = GridManager.Instance != null ? GridManager.Instance.GetTile(pos.x, pos.y) : null;
        return tile != null && tile.Outpost == OutpostType.Camp;
    }

    IEnumerator CampHealCoroutine()
    {
        while (!IsDead && IsCampTile(GridPos))
        {
            yield return new WaitForSeconds(CampHealInterval);
            if (IsDead || IsMoving || !IsCampTile(GridPos)) break;
            Heal(CampHealAmount);
        }
        _campHealCo = null;
    }

    // ---- 毒メカニクス ----

    public void ApplyPoison(int turns, AnimalUnit applier = null)
    {
        bool wasClean = _poisonedTurns <= 0;
        if (wasClean) _poisonApplier = applier;
        _poisonedTurns = Mathf.Max(_poisonedTurns, turns);
        if (_poisonIndicator != null) _poisonIndicator.SetActive(true);
        if (!SimMode && wasClean)
            StartPoisonLoop();
        if (!SimMode)
        {
            if (_poisonCo != null) StopCoroutine(_poisonCo);
            _poisonCo = StartCoroutine(PoisonTickCoroutine());
        }
    }

    void StartPoisonLoop()
    {
        if (_poisonLoopSrc != null && _poisonLoopSrc.isPlaying) return;
        _poisonLoopSrc = AudioPool.BorrowLoop(
            SoundAssetTable.Instance.sePoisonBack,
            0.6f * SoundManager.AnimalVolume);
    }

    void StopPoisonLoop()
    {
        AudioPool.ReturnLoop(ref _poisonLoopSrc);
    }

    // ターン制専用：ターン終了時に GameManager から呼ばれる
    public void TickPoison()
    {
        if (_poisonedTurns <= 0 || IsDead) return;
        CurrentHP = Mathf.Max(0, CurrentHP - AnimalDefinitions.SnakePoisonDamage);
        _poisonedTurns--;
        if (!SimMode) { UpdateHPBar(); ShowHPBar(); SoundManager.Poison(); }
        if (_poisonedTurns <= 0)
        {
            if (_poisonIndicator != null) _poisonIndicator.SetActive(false);
            StopPoisonLoop();
        }
        if (CurrentHP <= 0) Die(_poisonApplier);
    }

    IEnumerator PoisonTickCoroutine()
    {
        while (_poisonedTurns > 0 && !IsDead)
        {
            yield return new WaitForSeconds(AnimalDefinitions.SnakePoisonInterval);
            if (IsDead) break;
            CurrentHP = Mathf.Max(0, CurrentHP - AnimalDefinitions.SnakePoisonDamage);
            _poisonedTurns--;
            UpdateHPBar(); ShowHPBar(); SoundManager.Poison();
            if (_poisonedTurns <= 0)
            {
                if (_poisonIndicator != null) _poisonIndicator.SetActive(false);
                StopPoisonLoop();
            }
            if (CurrentHP <= 0) { Die(_poisonApplier); yield break; }
        }
        _poisonCo = null;
    }

    // ---- 霧制御 ----

    public void SetVisible(bool visible)
    {
        _isVisible = visible;
        foreach (Transform child in transform)
        {
            if (child.gameObject == _hpBarRoot || child.gameObject == _cdIndicator
                || child.gameObject == _poisonIndicator) continue;
            child.gameObject.SetActive(visible);
        }
        if (_cdIndicator != null)
            _cdIndicator.SetActive(visible && _moveCooldown > 0f && !IsDead);
        if (_poisonIndicator != null)
            _poisonIndicator.SetActive(visible && _poisonedTurns > 0 && !IsDead);
    }

    // ゾウ・キリンは進行方向に -0.2 オフセット（大きいモデルをマス内に収める）
    Vector3 TileOffset()
    {
        float back = (AnimalType == AnimalType.Elephant || AnimalType == AnimalType.Giraffe
                      || AnimalType == AnimalType.Hippo)
                     ? -0.2f * (Owner == 1 ? 1f : -1f) : 0f;
        return new Vector3(0f, 0.25f, back);
    }

    void SyncWorldPos()
    {
        if (GridManager.Instance == null) return;
        transform.position = GridManager.Instance.TileToWorld(GridPos.x, GridPos.y) + TileOffset();
    }
}
