using System;

[Serializable]
public class AIWeights
{
    public float FoxMult           = 1.5f;  // Fox攻撃優先倍率          [1~2]
    public float FoxGuardMult      = 1.0f;  // Fox護衛ボーナス倍率      [1~2]
    public float CampMult          = 1.0f;  // キャンプ回復指向倍率     [1~2]

    public float FleeThresholdMult = 1.0f;  // 低HP時の被攻撃ペナルティ倍率 [1~2]
    public float FormationBias     = 0.0f;  // チーム中心から何タイル前衛を好むか [-2~+2]（固定・学習対象外）

#if UNITY_EDITOR
    private static readonly System.Random _sharedRng = new System.Random();

    public AIWeights Clone() => (AIWeights)MemberwiseClone();

    public AIWeights Mutate(float scale = 0.1f, float rate = 0.08f)
    {
        var w = Clone();
        foreach (var f in typeof(AIWeights).GetFields())
        {
            if (f.Name == nameof(FormationBias)) continue;
            if ((float)_sharedRng.NextDouble() < rate)
            {
                float v = (float)f.GetValue(w);
                float nv = UnityEngine.Mathf.Clamp(v + (float)(_sharedRng.NextDouble() * 2 - 1) * scale, 0f, 4f);
                f.SetValue(w, nv);
            }
        }
        return w;
    }
#endif
}
