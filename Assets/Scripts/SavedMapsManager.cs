using System.Collections.Generic;
using System.IO;
using UnityEngine;

public static class SavedMapsManager
{
    public static bool UseSelectedMap = false;

    static string FilePath => Path.Combine(Application.persistentDataPath, "saved_maps.json");
    static List<int> _seeds;

    public static List<int> Seeds
    {
        get { if (_seeds == null) Load(); return _seeds; }
    }

    public static void Add(int seed)
    {
        if (!Seeds.Contains(seed)) { Seeds.Add(seed); Save(); }
    }

    public static void Remove(int seed)
    {
        if (Seeds.Remove(seed)) Save();
    }

    public static void Clear()
    {
        Seeds.Clear();
        Save();
    }

    public static bool Contains(int seed) => Seeds.Contains(seed);

    public static int GetRandom()
    {
        if (Seeds.Count == 0) return -1;
        return Seeds[Random.Range(0, Seeds.Count)];
    }

    static void Load()
    {
        _seeds = new List<int>();
        if (File.Exists(FilePath))
        {
            var data = JsonUtility.FromJson<SeedData>(File.ReadAllText(FilePath));
            if (data?.seeds != null) _seeds.AddRange(data.seeds);
        }
    }

    static void Save() =>
        File.WriteAllText(FilePath, JsonUtility.ToJson(new SeedData { seeds = Seeds.ToArray() }));

    [System.Serializable]
    class SeedData { public int[] seeds; }
}
