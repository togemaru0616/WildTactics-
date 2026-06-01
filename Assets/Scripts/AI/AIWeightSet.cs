using System;
using System.Collections.Generic;

[Serializable]
public class AIWeightSet
{
    // 各ユニットの重み (AnimalTypeと名前が一致するフィールド)
    public AIWeights Tiger       = new AIWeights();
    public AIWeights Bear        = new AIWeights();
    public AIWeights Gorilla     = new AIWeights();
    public AIWeights Lion        = new AIWeights();
    public AIWeights Chimpanzee  = new AIWeights();
    public AIWeights Rhino       = new AIWeights();
    public AIWeights Horse       = new AIWeights();
    public AIWeights Snake       = new AIWeights();
    public AIWeights Boar        = new AIWeights();
    public AIWeights Hippo       = new AIWeights();
    public AIWeights Giraffe     = new AIWeights();
    public AIWeights Elephant    = new AIWeights();
    public AIWeights Panda       = new AIWeights();
    public AIWeights Wolf        = new AIWeights();
    public AIWeights Reindeer    = new AIWeights();
    public AIWeights Fox         = new AIWeights();

    // ---- 探索モード（ScoreExplore で使用）----
    public float ExploreNewTileWeight  = 20f;  // 未探索タイル1枚あたりのスコア
    public float ExploreLhGradient     = 15f;  // 未制圧灯台への接近勾配（Combat-seeking でも流用）
    public float ExploreFormationBonus =  8f;  // 探索中に隊列を維持するボーナス
    public float ExploreExposurePen    = 25f;  // 移動中の無防備時間×最終確認脅威（機会損失ペナルティ）

    // ---- 探索モード + SimBoard共用（ScoreExplore・αβ探索・EvalEnemyBestResponse で使用）----
    public float LighthouseCapture     = 100f;  // 灯台制圧スコア

    // ---- SimBoard専用（αβ探索・EvalEnemyBestResponse で使用）----
    public float FoxKillReward         = 300f;  // 敵 Fox 撃破ボーナス
    public float DualAdvantageBonus    =  40f;  // HP優位かつ数的優位の複合シナジーボーナス

    // ---- 戦闘モード（ScoreCombat で使用）----
    // 攻撃・撃破
    public float SafeAttack            =  80f;  // 通常攻撃スコア
    public float DefenderFoxDist       =  35f;  // Fox護衛ボーナス（per-animal FoxGuardMult で拡縮）
    public float FoxDangerPen          = 180f;  // Fox射程内への移動ペナルティ（突進は×0.5）
    public float RangeAttackBonus      =  40f;  // 2マス射程攻撃ボーナス（安全圏から攻撃）
    public float NumericalPowerMult        =  12f;  // 数的優位乗数（劣位は×3.33倍ペナルティ）
    // 孫氏の兵法（勢）
    public float PressureShift         = 150f;   // 優勢(HP比)×最大バイアスシフト量（タイル数×100）

    // 地形・回復
    public float TerrainBonus          =  20f;  // 得意地形ボーナス
    public float CampBonus             =  50f;  // キャンプ回復スコア基準値（per-animal CampMult で拡縮）

    // 敵を見失った際の追跡（Combat seeking）
    public float SeekLastKnownBonus    =  20f;  // 最終確認位置へ接近するボーナス

    // ---- 隊列（横一列）----
    public float FormationSpreadPen    =  40f;  // 理想位置からのずれ²×この値×0.1をペナルティ（2次関数）

    // ---- 配置フェーズ（PlacementManager で使用）----
    public AnimalPickWeights AnimalPick       = new AnimalPickWeights();
    public float              TerrainPickScale = 30f;

    // ---- フレームワーク ----
    public AIWeightSet()
    {
        Fox.CampMult        = 2.0f;
        Panda.CampMult      = 1.8f;
        Horse.CampMult      = 1.6f;
        Snake.CampMult      = 1.5f;
        Reindeer.CampMult   = 1.5f;
        Giraffe.CampMult    = 1.5f;
        Chimpanzee.CampMult = 1.4f;
        Rhino.CampMult      = 1.3f;
        Bear.CampMult       = 1.2f;
        Boar.CampMult       = 1.2f;
        Gorilla.CampMult    = 1.2f;
        Wolf.CampMult       = 1.2f;
        Hippo.CampMult      = 1.1f;
        Elephant.CampMult   = 1.1f;
        Lion.CampMult       = 1.0f;
        Tiger.CampMult      = 1.0f;

        Fox.FormationBias      = -1.0f;  // 除外されるが記録用
        Panda.FormationBias    = -0.5f;
        Giraffe.FormationBias  =  0.0f;
        Snake.FormationBias    =  0.5f;
        Reindeer.FormationBias =  0.5f;
        Horse.FormationBias    =  1.0f;
        Chimpanzee.FormationBias = 1.0f;
        Wolf.FormationBias     =  1.0f;
        Bear.FormationBias     =  1.5f;
        Gorilla.FormationBias  =  1.5f;
        Rhino.FormationBias    =  1.5f;
        Hippo.FormationBias    =  1.5f;
        Elephant.FormationBias =  1.5f;
        Boar.FormationBias     =  2.0f;
        Lion.FormationBias     =  2.0f;
        Tiger.FormationBias    =  2.0f;
    }

    public AIWeights Get(AnimalType type)
    {
        var f = typeof(AIWeightSet).GetField(type.ToString());
        return f != null ? (AIWeights)f.GetValue(this) : new AIWeights();
    }

#if UNITY_EDITOR
    private static readonly Random _rng = new Random();

    public AIWeightSet Clone()
    {
        var s = new AIWeightSet();
        foreach (var f in typeof(AIWeightSet).GetFields())
        {
            if      (f.FieldType == typeof(AIWeights))
                f.SetValue(s, ((AIWeights)f.GetValue(this)).Clone());
            else if (f.FieldType == typeof(AnimalPickWeights))
                f.SetValue(s, ((AnimalPickWeights)f.GetValue(this)).Clone());
            else
                f.SetValue(s, f.GetValue(this));
        }
        return s;
    }

    static readonly HashSet<string> _animalPickFields = new() { nameof(TerrainPickScale) };

    // 自己対戦では対称性により学習不可能なフィールド（固定値を使用）
    static readonly HashSet<string> _fixedParams = new()
    {
        nameof(NumericalPowerMult),
    };

    // 負値になってはいけないフィールド（AI行動が逆転するため）
    static readonly HashSet<string> _nonNegativeFields = new()
    {
        nameof(ExploreNewTileWeight), nameof(ExploreLhGradient), nameof(ExploreFormationBonus),
        nameof(LighthouseCapture), nameof(FoxKillReward), nameof(DualAdvantageBonus),
        nameof(SafeAttack), nameof(DefenderFoxDist), nameof(FoxDangerPen),
        nameof(RangeAttackBonus), nameof(NumericalPowerMult), nameof(PressureShift),
        nameof(TerrainBonus), nameof(CampBonus), nameof(SeekLastKnownBonus),
        nameof(FormationSpreadPen), nameof(TerrainPickScale),
    };

    public AIWeightSet Mutate(float scale = 10f, float rate = 0.1f,
                              MutationGroup groups = MutationGroup.All)
    {
        var s = Clone();
        foreach (var f in typeof(AIWeightSet).GetFields())
        {
            if (f.FieldType == typeof(AIWeights))
            {
                if (groups.HasFlag(MutationGroup.AnimalWeights))
                    f.SetValue(s, ((AIWeights)f.GetValue(this)).Mutate(scale, rate));
            }
            else if (f.FieldType == typeof(AnimalPickWeights))
            {
                if (groups.HasFlag(MutationGroup.AnimalPick))
                    f.SetValue(s, ((AnimalPickWeights)f.GetValue(this)).Mutate(scale, rate));
            }
            else if (f.FieldType == typeof(float))
            {
                if (_fixedParams.Contains(f.Name)) continue;
                bool groupAllowed = _animalPickFields.Contains(f.Name) ? groups.HasFlag(MutationGroup.AnimalPick)
                                  :                                       groups.HasFlag(MutationGroup.CommonFloat);
                if (groupAllowed && (float)_rng.NextDouble() < rate)
                {
                    float v  = (float)f.GetValue(this);
                    float nv = v + (float)(_rng.NextDouble() * 2 - 1) * scale;
                    if (_nonNegativeFields.Contains(f.Name)) nv = Math.Max(0f, nv);
                    f.SetValue(s, nv);
                }
            }
        }
        return s;
    }

    // 1動物のみ変異させたクローンを返す（AnimalWeights 個別学習用）
    public AIWeightSet MutateSingleAnimal(AnimalType target, float scale, float rate)
    {
        var s  = Clone();
        var fi = typeof(AIWeightSet).GetField(target.ToString());
        if (fi != null && fi.FieldType == typeof(AIWeights))
            fi.SetValue(s, ((AIWeights)fi.GetValue(this)).Mutate(scale, rate));
        return s;
    }
#endif
}
