using System.Runtime.InteropServices;

// Blittable (GC-free) value-type mirror of AIWeights for use in GC-pressure-sensitive contexts.
[StructLayout(LayoutKind.Sequential)]
public struct AnimalWeightsBlittable
{

    public float FoxMult;
    public float FoxGuardMult;
    public float CampMult;


    public float FleeThresholdMult;
    public float FormationBias;

    public static AnimalWeightsBlittable From(AIWeights w) => new()
    {

        FoxMult           = w.FoxMult,
        FoxGuardMult      = w.FoxGuardMult,
        CampMult          = w.CampMult,


        FleeThresholdMult = w.FleeThresholdMult,
        FormationBias     = w.FormationBias,
    };
}

// Blittable mirror of AIWeightSet — 16 per-animal weight slots + shared params.
// Use SimWeightsBlittable.From(AIWeightSet) to convert from the managed version.
[StructLayout(LayoutKind.Sequential)]
public struct SimWeightsBlittable
{
    // Per-animal weights (field order matches AnimalType enum: Fox=0 … Reindeer=15)
    public AnimalWeightsBlittable Fox;
    public AnimalWeightsBlittable Tiger;
    public AnimalWeightsBlittable Bear;
    public AnimalWeightsBlittable Gorilla;
    public AnimalWeightsBlittable Lion;
    public AnimalWeightsBlittable Chimpanzee;
    public AnimalWeightsBlittable Rhino;
    public AnimalWeightsBlittable Horse;
    public AnimalWeightsBlittable Snake;
    public AnimalWeightsBlittable Boar;
    public AnimalWeightsBlittable Hippo;
    public AnimalWeightsBlittable Giraffe;
    public AnimalWeightsBlittable Elephant;
    public AnimalWeightsBlittable Panda;
    public AnimalWeightsBlittable Wolf;
    public AnimalWeightsBlittable Reindeer;

    // Shared weights (mirrors public fields of AIWeightSet)
    public float SafeAttack;
    public float DefenderFoxDist;
    public float LighthouseCapture;

    public float FoxDangerPen;

    public float NumericalPowerMult;
    public float RangeAttackBonus;
    public float PressureShift;
    public float TerrainBonus;
    public float CampBonus;
    public float FoxKillReward;
    public float DualAdvantageBonus;

    public AnimalWeightsBlittable GetAnimal(AnimalType type) => type switch
    {
        AnimalType.Fox        => Fox,
        AnimalType.Tiger      => Tiger,
        AnimalType.Bear       => Bear,
        AnimalType.Gorilla    => Gorilla,
        AnimalType.Lion       => Lion,
        AnimalType.Chimpanzee => Chimpanzee,
        AnimalType.Rhino      => Rhino,
        AnimalType.Horse      => Horse,
        AnimalType.Snake      => Snake,
        AnimalType.Boar       => Boar,
        AnimalType.Hippo      => Hippo,
        AnimalType.Giraffe    => Giraffe,
        AnimalType.Elephant   => Elephant,
        AnimalType.Panda      => Panda,
        AnimalType.Wolf       => Wolf,
        AnimalType.Reindeer   => Reindeer,
        _                     => default,
    };

    public static SimWeightsBlittable From(AIWeightSet w) => new()
    {
        Fox        = AnimalWeightsBlittable.From(w.Fox),
        Tiger      = AnimalWeightsBlittable.From(w.Tiger),
        Bear       = AnimalWeightsBlittable.From(w.Bear),
        Gorilla    = AnimalWeightsBlittable.From(w.Gorilla),
        Lion       = AnimalWeightsBlittable.From(w.Lion),
        Chimpanzee = AnimalWeightsBlittable.From(w.Chimpanzee),
        Rhino      = AnimalWeightsBlittable.From(w.Rhino),
        Horse      = AnimalWeightsBlittable.From(w.Horse),
        Snake      = AnimalWeightsBlittable.From(w.Snake),
        Boar       = AnimalWeightsBlittable.From(w.Boar),
        Hippo      = AnimalWeightsBlittable.From(w.Hippo),
        Giraffe    = AnimalWeightsBlittable.From(w.Giraffe),
        Elephant   = AnimalWeightsBlittable.From(w.Elephant),
        Panda      = AnimalWeightsBlittable.From(w.Panda),
        Wolf       = AnimalWeightsBlittable.From(w.Wolf),
        Reindeer   = AnimalWeightsBlittable.From(w.Reindeer),

        SafeAttack               = w.SafeAttack,
        DefenderFoxDist          = w.DefenderFoxDist,
        LighthouseCapture        = w.LighthouseCapture,
        FoxDangerPen             = w.FoxDangerPen,

        NumericalPowerMult       = w.NumericalPowerMult,
        RangeAttackBonus         = w.RangeAttackBonus,
        PressureShift            = w.PressureShift,
        TerrainBonus             = w.TerrainBonus,
        CampBonus                = w.CampBonus,
        FoxKillReward            = w.FoxKillReward,
        DualAdvantageBonus       = w.DualAdvantageBonus,
    };
}
