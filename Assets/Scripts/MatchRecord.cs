using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class MatchRecord
{
    public string playerName;
    public bool   won;
    public string date;

    public MatchRecord(string playerName, bool won, string date)
    {
        this.playerName = playerName;
        this.won        = won;
        this.date       = date;
    }
}

[Serializable]
class MatchRecordList { public List<MatchRecord> records = new List<MatchRecord>(); }

public static class MatchHistoryManager
{
    const string FileName = "match_history.json";
    const int    MaxSave  = 50;

    static string Path => System.IO.Path.Combine(Application.persistentDataPath, FileName);

    public static void Save(MatchRecord record)
    {
        var list = Load();
        list.Insert(0, record);
        if (list.Count > MaxSave) list.RemoveRange(MaxSave, list.Count - MaxSave);
        var wrapper = new MatchRecordList { records = list };
        System.IO.File.WriteAllText(Path, JsonUtility.ToJson(wrapper, true));
    }

    public static List<MatchRecord> Load()
    {
        if (!System.IO.File.Exists(Path)) return new List<MatchRecord>();
        try
        {
            var json = System.IO.File.ReadAllText(Path);
            return JsonUtility.FromJson<MatchRecordList>(json)?.records ?? new List<MatchRecord>();
        }
        catch { return new List<MatchRecord>(); }
    }

    public static int GetWinStreak()
    {
        int streak = 0;
        foreach (var r in Load())
        {
            if (r.won) streak++;
            else break;
        }
        return streak;
    }
}
