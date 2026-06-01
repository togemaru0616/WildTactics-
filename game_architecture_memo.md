# WildTactics ゲームアーキテクチャ メモ
## 最終更新: 2026-04-27

---

## ゲーム概要
- ジャンル: トップダウン 2D 将棋ライクストラテジー（Unity URP）
- グリッド: 13列(COLS) × 26行(ROWS)
- 陣地(HOME): x=4〜8、z=0〜1 (P1) / z=24〜25 (P2)、`BASE_ROWS=2`
- タイルサイズ: TileSize=1.0、TileGap=0.02
- フェーズ: Placement（配置） → Battle（バトル）

---

## フェーズ別の流れ

### 配置フェーズ (PlacementManager.cs)
- カードトレイからドラッグ＆ドロップで陣地に動物を配置
- ズームカメラ(5×2) + 全体マップ + 移動パターン表示 + キャラ説明文
- コスト上限: 130pt / 最大 10 体（Fox 事前配置 1 体込み）
  - S ランク=20pt, A ランク=10pt, B ランク=5pt
- 時間制限: 120 秒（TotalTime）
- 既設ユニットへのドロップ → 置き換え可能
- バトル開始時: GridManager.ClearHomeHighlight() で陣地色リセット

### バトルフェーズ (InputHandler.cs)
- タップ選択 → 移動/攻撃ハイライト → タップで実行
- 移動タイル: 緑、攻撃タイル: 赤、選択リング: 黄
- CanAct = !IsDead && !IsActing && _cooldown<=0 のときのみタイル表示
- 移動後クールダウン = AttackCooldown × 0.5
- 攻撃後クールダウン = AttackCooldown

---

## スクリプト一覧と役割

| ファイル | 役割 |
|---|---|
| SceneSetup.cs | 起動時に TitleManager と EventSystem を生成 |
| TitleManager.cs | タイトル画面 UI / VS CPU でゲーム開始 |
| GameSetup.cs | ゲーム全オブジェクトを生成・カメラ初期化 |
| GameBootstrap.cs | 空（将来用） |
| GameManager.cs | フェーズ管理・カメラ切替・StartBattle() |
| GamePhase.cs | enum: Placement / Battle |
| GridManager.cs | タイル生成・テクスチャ割当・ホームカラー管理 |
| TerrainGenerator.cs | プロシージャル地形生成（川・森・岩・砂浜・城・野営地） |
| TileCell.cs | タイルデータクラス（座標・地形・拠点） |
| TerrainType.cs | enum: Flat/Rocky/Forest/River/Bridge/Pond/Sand |
| OutpostType.cs | enum: None/Castle/Camp |
| OutpostManager.cs | 城・野営地の占領処理・マーカー表示 |
| FogManager.cs | P1 視点の霧システム（半透明・視界外は敵ユニット非表示） |
| GameCamera.cs | バトルカメラ（ドラッグスクロール・配置中は描画オフ） |
| GridPathfinder.cs | A* 経路探索（現在は未使用、将来 AI 用） |
| AnimalType.cs | enum: Fox/Tiger/Bear … 17 種 |
| AnimalRank.cs | enum: S/A/B |
| AnimalDefinitions.cs | 移動パターン・地形倍率・攻撃範囲・視界・速度・HP・攻撃力・ランク・コスト |
| AnimalUnit.cs | ユニット本体：移動/攻撃/HP バー/アニメーション/サウンド |
| UnitManager.cs | 全ユニット管理・グリッド占有・SpawnUnit |
| AnimalAssetTable.cs | ScriptableObject: 動物プレハブ・アイコン・サウンドのテーブル |
| InputHandler.cs | バトルフェーズ：タップ入力・ハイライト・移動/攻撃指令 |
| PlacementManager.cs | 配置フェーズ UI 全体（RT カメラ・カードトレイ・置き替え） |
| PlacementCard.cs | カードのドラッグ処理（縦ドラッグ=配置、横=スクロール） |
| Editor/AnimalPrefabCleaner.cs | Tools → Clean Animal Prefabs（Wander/NavMesh 除去） |

---

## AnimalUnit の状態機械

```
Idle ──[MoveTo()]──> Walk ──[到着]──> Idle
     ──[AttackTarget()]──> Attack ──[完了]──> Idle
     ──[TakeDamage() → HP=0]──> Death
```

- `CanAct` が false のときはタイルを表示しない
- 移動中に死亡 → コルーチン break
- 向き: P1=+Z向き、P2=-Z向き。行動後に RestoreDefaultFacing() でリセット

---

## サウンドシステム

- Polyperfect の `AnimalPlaySound` / `Common_PlaySound` を **リフレクションで** 呼ぶ
- Awake で `AudioSource.playOnAwake=false` / `Stop()` → 基本沈黙
- 発音タイミング: タップ時(Idle)、攻撃時(Attack)、被弾時(Hurt)、死亡時(Death)
- WanderScript 削除済み → 自動環境音なし

---

## アニメーション制御 (AnimalUnit.SetAnim)

Polyperfect Animator の標準パラメータを `TrySet*()` で安全にセット:

| AnimState | float Speed | bool Walking | bool Attacking | trigger Attack | bool/trigger Death |
|---|---|---|---|---|---|
| Idle   | 0.0 | false | false | - | - |
| Walk   | 0.5 | true  | -     | - | - |
| Attack | -   | -     | true  | ✓ | - |
| Death  | -   | -     | -     | - | ✓ |

---

## HP・攻撃力（暫定値、後で調整）

| 動物 | HP | ATK |
|---|---|---|
| Elephant | 250 | 55 |
| Hippo    | 220 | 45 |
| Gorilla  | 200 | 48 |
| Bear     | 190 | 44 |
| Rhino    | 175 | 42 |
| Lion     | 160 | 40 |
| Panda    | 155 | 22 |
| Tiger    | 145 | 50 |
| Horse    | 145 | 30 |
| Boar     | 130 | 38 |
| Wolf     | 125 | 32 |
| Giraffe  | 125 | 25 |
| Chimp    | 115 | 28 |
| Reindeer | 115 | 20 |
| Snake    | 100 | 36 |
| Goat     | 100 | 18 |
| Fox      |  90 | 15 |

---
