# WildTactics AI 仕様書

## 概要

ルールベースAI（AICommander）がSimBoard（自己対戦シミュレーター）と共通のスコアリング（CombatScorer）を使って行動を決定する。パラメータは自己対戦で進化的学習を行う。

---

## ファイル構成

| ファイル | 役割 |
|---|---|
| `AICommander.cs` | ゲーム本体のAI制御（視野・モード切替・コマンド発行） |
| `CombatScorer.cs` | 移動スコア計算（AICommander・SimBoard共通） |
| `AIWeights.cs` | 動物ごとの重みパラメータ |
| `AIWeightSet.cs` | 共通スコアパラメータ＋動物別AIWeights |
| `SimWeightsBlittable.cs` | SimBoard用のGCフリー構造体ミラー |
| `SimBoard.cs` | 自己対戦シミュレーター |
| `GameSimulator.cs` | 学習ループ・CSV出力・best_weights.json保存 |

---

## 動物ごとの重み（AIWeights）

各動物に個別設定。学習範囲 **[0, 4]**。

| フィールド | 意味 |
|---|---|
| `FoxMult` | 敵Fox狙い優先倍率 |
| `FoxGuardMult` | 味方Fox護衛ボーナス倍率 |
| `CampMult` | キャンプ回復指向倍率 |
| `FleeThresholdMult` | 低HP時の被攻撃ペナルティ倍率 |
| `FormationBias` | 隊列中心から何タイル前衛を好むか（**固定・学習対象外**）|

### FormationBias 初期値

| 動物 | 値 | 動物 | 値 |
|---|---|---|---|
| Fox | -2.0 | Horse | 0.0 |
| Panda | -1.5 | Chimpanzee | 0.0 |
| Giraffe | -1.0 | Wolf | 0.0 |
| Snake | -0.5 | Bear | +0.5 |
| Reindeer | -0.5 | Gorilla | +0.5 |
| | | Rhino | +0.5 |
| | | Hippo | +0.5 |
| | | Elephant | +0.5 |
| | | Boar | +1.0 |
| | | Lion | +1.0 |
| | | Tiger | +1.0 |

---

## 共通スコアパラメータ（AIWeightSet）

### 探索モード

| パラメータ | 初期値 | 意味 |
|---|---|---|
| `ExploreNewTileWeight` | 20 | 未探索タイル1枚あたりのスコア |
| `ExploreLhGradient` | 15 | 未制圧灯台への接近勾配 |
| `ExploreFormationBonus` | 8 | 探索中の隊列維持ボーナス |
| `ExploreExposurePen` | 25 | 移動中の無防備時間ペナルティ |
| `LighthouseCapture` | 100 | 灯台制圧スコア |

### 戦闘モード

| パラメータ | 初期値 | 意味 |
|---|---|---|
| `SafeAttack` | 80 | 通常攻撃スコア基準値 |
| `DefenderFoxDist` | 35 | Fox護衛ボーナス基準値 |
| `FoxDangerPen` | 180 | FoxがこちらのFoxに隣接するペナルティ |
| `RangeAttackBonus` | 40 | 2マス射程攻撃ボーナス |
| `NumericalPowerMult` | 12 | 数的優位乗数（**固定・学習対象外**） |
| `TerrainBonus` | 20 | 得意地形ボーナス |
| `CampBonus` | 50 | キャンプ回復スコア基準値 |
| `SeekLastKnownBonus` | 20 | 敵最終確認位置への追跡ボーナス |

### 隊列・圧力

| パラメータ | 初期値 | 実効値 | 意味 |
|---|---|---|---|
| `FormationSpreadPen` | 40 | ×0.1で使用 | 理想位置からのずれ²×この値×0.1をペナルティ |
| `PressureShift` | 150 | ×0.01で使用 | 優勢時の全体バイアスシフト量（タイル数×100） |

### SimBoard専用

| パラメータ | 初期値 | 意味 |
|---|---|---|
| `FoxKillReward` | 300 | 敵Fox撃破ボーナス |
| `DualAdvantageBonus` | 40 | HP優位＋数的優位の複合シナジー |

### 配置フェーズ

| パラメータ | 初期値 | 意味 |
|---|---|---|
| `AnimalPick` | 動物別 | 配置時の動物選択重み |
| `TerrainPickScale` | 30 | 配置時の地形選択スケール |

---

## スコアリング（CombatScorer.Score）

各移動先 `dest` に対して以下を積算する。

### 1. 攻撃スコア
```
atk = canWin ? SafeAttack : -SafeAttack × 0.25
atk = Foxを攻撃 ? SafeAttack × FoxMult : atk
atk += ランチェスター優位補正（NumericalPowerMult）
atk += 撃破進捗ボーナス（killProg²×50）
score += atk
```

### 2. 射程攻撃ボーナス
```
score += RangeAttackBonus  （2マス射程攻撃時）
```

### 3. 突進（Charge）
突進で敵を押し出せる場合、攻撃スコアと同様に加算。

### 4. Fox護衛
```
d = dest から味方Fox への距離
d≤3 → score += DefenderFoxDist × FoxGuardMult
d≤5 → score += DefenderFoxDist × FoxGuardMult × (5-d)/2
d>5 → 加算なし（+ Fox周囲脅威グラジェント）
```

### 5. 地形ボーナス
```
score += TerrainBonus  （得意地形に移動する場合）
```

### 6. キャンプ回復
```
campUrg = 1 - hpRatio
score += CampMult × campUrg × (1 / (距離+1)) × CampBonus
```

### 7. 隊列・圧力（統合）

Fox を除くユニットには formation penalty を適用しない。Fox 自身は FormationBias=-1.0 で自分の現在位置より後方を理想とする。

```
hpAdv = (allyHP - enemyHP) / (allyHP + enemyHP + 1)   // -1.0〜+1.0

// 基準Z：味方Fox位置（いなければチーム平均）
refZ = foxPos.y  （or GetTeamZCenter）

// 前後位置（前方が正、P1/P2を fSign で吸収）
actualFwd = (dest.y - refZ) × fSign

// 理想位置（FormationBias + 優勢による全体シフト）
effectiveBias = FormationBias + hpAdv × PressureShift × 0.01

// 理想位置からのずれ（正=前方超過、負=後方不足）
deviation = actualFwd - effectiveBias

// 前方超過ペナルティは優勢時に緩和（劣勢時はクランプで1.0固定）
fwdDamp = deviation > 0 ? Clamp01(1 - hpAdv) : 1.0

score -= deviation² × FormationSpreadPen × 0.1 × fwdDamp
```

| 状況 | fwdDamp | 前方超過ペナルティ |
|---|---|---|
| 劣勢・互角 | 1.0 | 通常 |
| 優勢（hpAdv=0.7） | 0.3 | 70%減 |
| 圧倒（hpAdv=1.0） | 0.0 | なし → Fox追跡できる |

**FormationBias の意味（味方Fox位置を基準）**

| 動物 | Bias | 理想位置 |
|---|---|---|
| Fox | -1.0 | 現在位置の1マス後方（自身） |
| Panda | -0.5 | Foxの0.5マス後方 |
| Giraffe | 0.0 | Foxと同位置 |
| Wolf/Horse/Chimpanzee | +1.0 | Foxの1マス前方（中立） |
| Bear/Gorilla/Rhino/Hippo/Elephant | +1.5 | Foxの1.5マス前方 |
| Tiger/Lion/Boar | +2.0 | Foxの2マス前方（前衛） |

### 8. 機会損失コスト
現在位置から攻撃できる場合、移動中の無防備時間をペナルティ化。

### 9. 被攻撃ペナルティ
敵が `dest` に攻撃できる場合、FleeThresholdMult で拡縮してペナルティ。

---

## AIモード

| モード | 条件 | スコア関数 |
|---|---|---|
| Explore | 可視敵なし・初期 | ScoreExplore |
| Combat | 可視敵あり | CombatScorer.Score |
| Combat（追跡） | 敵を見失って8秒以内 | SeekLastKnown + 灯台グラジェント |

---

## 行動順序（CommandLoop）

1. `CanMove` な全ユニットの**現在位置スコア**を計算
2. スコアが**低い順**（最も状況が悪いユニット優先）にソート
3. 1フレームに1体ずつ `FallbackGreedy` で最善手を選んで移動
4. キューが空になると再構築（最大50msラグ）

---

## 深さ2探索（depth-2）

敵ユニットが3マス以内にいる場合、`EvalEnemyBestResponse` で敵の最善応手を評価し、`score - discount × enemyBestScore` で手を選ぶ。

---

## 学習システム

### MutationGroup

| モード | 対象 |
|---|---|
| `AnimalWeights` | 動物別 FoxMult・FoxGuardMult・CampMult・FleeThresholdMult |
| `Common` | 共通スコアパラメータ（PressureShift・FormationSpreadPen等） |
| `AnimalPick` | 配置フェーズの動物選択重み |
| `Full` | 全パラメータ |

### 学習パラメータ範囲

- **動物別Mult**: [0, 4]（FormationBiasは固定で学習対象外）
- **共通Float**: [0, ∞)（_nonNegativeFieldsでクランプ）
- **デフォルトMutationScale**: 10.0（PressureShift・FormationSpreadPenは×10/×100スケールで対応）

### best_weights.json

`C:/Users/shou5/AppData/LocalLow/DefaultCompany/WildTactics/best_weights.json` に保存。
学習再開時はこのファイルを起点にする。コード変更でフィールドが変わった場合は手動で修正が必要。
