using System;

[Serializable]
public class AnimalPickWeights
{
    // Fox は必須なので除外、残り15体に重みをつける
    public float Tiger      = 10f;
    public float Bear       = 10f;
    public float Gorilla    = 10f;
    public float Lion       = 10f;
    public float Chimpanzee = 10f;
    public float Rhino      = 10f;
    public float Horse      = 10f;
    public float Snake      = 10f;
    public float Boar       = 10f;
    public float Hippo      = 10f;
    public float Giraffe    = 10f;
    public float Elephant   = 10f;
    public float Panda      = 10f;
    public float Wolf       = 10f;
    public float Reindeer   = 10f;

    public float Get(AnimalType type)
    {
        var f = typeof(AnimalPickWeights).GetField(type.ToString());
        return f != null ? (float)f.GetValue(this) : 10f;
    }

#if UNITY_EDITOR
    private static readonly Random _rng = new Random();

    public AnimalPickWeights Clone() => (AnimalPickWeights)MemberwiseClone();

    public AnimalPickWeights Mutate(float scale = 5f, float rate = 0.08f)
    {
        var w = Clone();
        foreach (var f in typeof(AnimalPickWeights).GetFields())
        {
            if (f.FieldType != typeof(float)) continue;
            if ((float)_rng.NextDouble() < rate)
            {
                float v = (float)f.GetValue(w);
                f.SetValue(w, v + (float)(_rng.NextDouble() * 2 - 1) * scale);
            }
        }
        return w;
    }
#endif
}
