using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "WildTactics/AnimalAssetTable")]
public class AnimalAssetTable : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        public AnimalType type;
        public GameObject prefab;        // visualPrefab
        public float      visualScale;   // > 0
        public AudioClip  attackClip;
        public AudioClip  deathClip;
        [Range(0.1f, 1.5f)]
        public float      attackVolumeMult = 1f; // 攻撃音の相対音量
    }

    public List<Entry> entries = new();

    static AnimalAssetTable _instance;
    public static AnimalAssetTable Instance
    {
        get
        {
            if (_instance == null)
                _instance = Resources.Load<AnimalAssetTable>("AnimalAssetTable");
            return _instance;
        }
    }

    Dictionary<AnimalType, Entry> _map;

    void OnEnable()
    {
        _map = new();
        foreach (var e in entries) _map[e.type] = e;
    }

    public Entry Get(AnimalType type)
        => _map != null && _map.TryGetValue(type, out var e) ? e : null;
}
