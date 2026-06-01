#if UNITY_EDITOR
using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

public enum ExperimentMode
{
    Full,         // 全パラメータ学習（従来）
    Baseline,     // 変異なし：デフォルト値の基準測定
    Tactical,     // 戦術パラメータ（未使用）
    AnimalWeights,// ユニット別 AIWeights
    AnimalPick,   // AnimalPickWeights
    Common,       // 共通 float パラメータ
}

[System.Flags]
public enum MutationGroup
{
    None = 0,
    Tactical = 1 << 0,
    AnimalWeights = 1 << 1,
    AnimalPick = 1 << 2,
    CommonFloat = 1 << 3,
    All = Tactical | AnimalWeights | AnimalPick | CommonFloat,
}

// Inspector の歯車メニューから「Run Optimization」を選ぶだけで最適化が走る
public class GameSimulator : MonoBehaviour
{
    public static GameSimulator Instance { get; private set; }

    [Header("Optimization Settings")]
    public int Iterations = 200;
    public int TrialsPerCheck = 20;
    public int BaseIteration = 50;  // AnimalWeights モード: 1動物あたりのイテレーション数（合計 = BaseIteration × 動物数）

    [Header("SGDR Schedule")]
    public float ScaleMax = 10f;  // サイクル開始時の変異幅（大きくかき混ぜる）
    public float ScaleMin = 0.5f; // サイクル末の変異幅（微調整）
    public int CycleLength = 50;   // 1サイクルのイテレーション数
    public float CycleMult = 2f;  // 再スタート後のサイクル長倍率
    public float MutateRate = 0.1f; // 変異させるフィールドの割合

    [Header("Experiment")]
    public ExperimentMode Experiment = ExperimentMode.Full;

    [Header("Map Baking")]
    public int BakeMapCount = 16; // Warm-up で事前生成する地形数

    [Header("Hall of Fame")]
    public int HallOfFameSize   = 5;   // 保持する過去チャンピオン数
    public int HallOfFameTrials = 20;  // HoF各メンバーへの試合数

    // GA Hall of Fame（過去のチャンピオン重みセット）
    readonly List<AIWeightSet> _hallOfFame = new();

    // UI から参照して「Baking Maps... 3/16」などを表示できる
    public string StatusText { get; private set; } = "";

    const int MAX_LOG_LINES = 1000; // CSVの最大行数（超えたら古い行を削除）

    readonly List<string> _logBuffer = new();

    static readonly AnimalType[] AllCandidates =
    {
        AnimalType.Tiger,    AnimalType.Bear,    AnimalType.Gorilla,   AnimalType.Lion,
        AnimalType.Chimpanzee, AnimalType.Rhino, AnimalType.Horse,    AnimalType.Snake,
        AnimalType.Boar,     AnimalType.Hippo,   AnimalType.Giraffe,  AnimalType.Elephant,
        AnimalType.Panda,    AnimalType.Wolf,    AnimalType.Reindeer,
    };

    const int SIM_SEED = 42;
    // イベント駆動: 1イベント = 1ユニットの行動。20ユニット×100行動=2000でほぼ全試合終結
    const int MAX_TICKS = 2000;

    // リフレクションキャッシュ
    static readonly FieldInfo[] AIWeightsFields = typeof(AIWeights).GetFields();
    static readonly FieldInfo[] AnimalPickFields = typeof(AnimalPickWeights).GetFields();

    // SimBoard プール
    static readonly ConcurrentBag<SimBoard> _boardPool = new();
    static SimBoard RentBoard() => _boardPool.TryTake(out var b) ? b : new SimBoard();
    static void ReturnBoard(SimBoard b) => _boardPool.Add(b);

    // 事前ベイク済みマップキャッシュ
    struct BakedMap
    {
        public SimTile[,] Tiles;
        public Vector2Int[] Lighthouses;
    }
    readonly List<BakedMap> _bakedMaps = new();
    int _randomBaseSeed;

    void Awake() => Instance = this;

    // ---- エントリーポイント ----

    [ContextMenu("Run Optimization")]
    public void StartOptimization() => StartCoroutine(OptimizationLoop());

    // ---- ヒルクライム最適化 ----

    IEnumerator OptimizationLoop()
    {
        // Warm-up：地形をまとめてベイク
        yield return StartCoroutine(BakeMapsPhase());

        // 実験モードに応じたファイル名・変異グループを決定
        string expSuffix = Experiment == ExperimentMode.Full ? "" : $"_{Experiment}";
        string weightsFile = "best_weights.json";
        string logFile = $"weights_log{expSuffix}.csv";
        MutationGroup groups = Experiment switch
        {
            ExperimentMode.Tactical => MutationGroup.Tactical,
            ExperimentMode.AnimalWeights => MutationGroup.AnimalWeights,
            ExperimentMode.AnimalPick => MutationGroup.AnimalPick,
            ExperimentMode.Common => MutationGroup.CommonFloat,
            _ => MutationGroup.All,  // Full: 全フィールド / Baseline: 変異なし
        };

        var best = LoadWeights(weightsFile) ?? new AIWeightSet();
        _hallOfFame.Clear();
        _hallOfFame.Add(new AIWeightSet()); // デフォルト重みでHoFを初期化（序盤の空判定防止）
        Debug.Log($"[Opt] Start  mode={Experiment}（前回の続きから再開）");

        EnsureLogHeader(logFile);
        TrimLog(logFile);
        int totalIter = GetLogLineCount(logFile);
        int adopted = 0;
        int rejectRun = 0;

        if (Experiment == ExperimentMode.AnimalWeights)
        {
            // ── 動物ごとに個別学習（合計 = BaseIteration × 動物数）──
            var animalsToTrain = new List<AnimalType>(AllCandidates) { AnimalType.Fox };
            int totalAnimals = animalsToTrain.Count;
            int grandTotal = BaseIteration * totalAnimals;
            Debug.Log($"[Opt] AnimalWeights: {totalAnimals}匹 × {BaseIteration}iter = {grandTotal}iter");

            for (int animalIdx = 0; animalIdx < totalAnimals; animalIdx++)
            {
                var target = animalsToTrain[animalIdx];
                int cyclePos = 0;
                float curCycleLen = CycleLength;
                rejectRun = 0;

                Debug.Log($"[Opt] ── {target} ({animalIdx + 1}/{totalAnimals}) 開始 ──");

                for (int i = 0; i < BaseIteration; i++)
                {
                    totalIter++;

                    float sgdrScale = ScaleMin + 0.5f * (ScaleMax - ScaleMin)
                        * (1f + Mathf.Cos(Mathf.PI * cyclePos / curCycleLen));

                    var candidate = best.MutateSingleAnimal(target, sgdrScale, MutateRate);

                    int wA = 0, wB = 0, d = 0;
                    yield return StartCoroutine(ParallelTrialLoop(candidate, best, TrialsPerCheck,
                        animalIdx * BaseIteration + i,
                        (a, b, draws) => { wA = a; wB = b; d = draws; },
                        forcedAnimal: target));

                    float winRate = (wA + d * 0.5f) / TrialsPerCheck;
                    if (winRate > 0.55f)
                    {
                        bool hofOk = true; float hofRate = 1f;
                        yield return StartCoroutine(CheckHallOfFame(candidate,
                            animalIdx * BaseIteration + i,
                            (ok, rate) => { hofOk = ok; hofRate = rate; }));
                        if (hofOk)
                        {
                            _hallOfFame.Add(best.Clone());
                            if (_hallOfFame.Count > HallOfFameSize) _hallOfFame.RemoveAt(0);
                            best = candidate;
                            adopted++;
                            _logBuffer.Add(BuildLogLine(totalIter, winRate, best));
                            FlushLogBuffer(logFile);
                            SaveWeights(best, weightsFile);
                            string rejectStr = rejectRun > 0 ? $"  (却下×{rejectRun})" : "";
                            Debug.Log($"[Opt] {target} {i + 1}/{BaseIteration}  採用#{adopted}  winRate={winRate:F2}  hofRate={hofRate:F2}{rejectStr}");
                            rejectRun = 0;
                        }
                        else
                        {
                            rejectRun++;
                            Debug.Log($"[Opt] {target} {i + 1}/{BaseIteration}  HoF不合格  hofRate={hofRate:F2}");
                        }
                    }
                    else
                    {
                        rejectRun++;
                        if (rejectRun % 20 == 0)
                            Debug.Log($"[Opt] {target} {i + 1}/{BaseIteration}  却下×{rejectRun}件連続");
                    }

                    cyclePos++;
                    if (cyclePos >= (int)curCycleLen)
                    {
                        cyclePos = 0;
                        curCycleLen = Mathf.Max(curCycleLen * CycleMult, 2f);
                        string rejectStr = rejectRun > 0 ? $"  (却下×{rejectRun})" : "";
                        Debug.Log($"[Opt] ★ {target} ウォームリスタート  次サイクル={curCycleLen}  scale→{ScaleMax:F1}{rejectStr}");
                    }
                }
            }

            if (rejectRun > 0)
                Debug.Log($"[Opt] 最終{rejectRun}件却下");

            FlushLogBuffer(logFile);
            SaveWeights(best, weightsFile);
            Debug.Log($"[Opt] Done  採用={adopted}/{grandTotal}  採用率={(float)adopted / grandTotal:P0}");
        }
        else
        {
            // ── 通常ヒルクライム（Tactical / AnimalPick / Common / Full / Baseline）──
            int cyclePos = 0;
            float curCycleLen = CycleLength;
            float baselineWinSum = 0f;
            int baselineCount = 0;

            for (int i = 0; i < Iterations; i++)
            {
                totalIter++;

                float sgdrScale = ScaleMin + 0.5f * (ScaleMax - ScaleMin)
                    * (1f + Mathf.Cos(Mathf.PI * cyclePos / curCycleLen));

                var candidate = Experiment == ExperimentMode.Baseline
                    ? best  // 変異なし：公平性テスト
                    : best.Mutate(sgdrScale, MutateRate, groups);

                int wA = 0, wB = 0, d = 0;
                yield return StartCoroutine(ParallelTrialLoop(candidate, best, TrialsPerCheck, i,
                    (a, b, draws) => { wA = a; wB = b; d = draws; }));

                float winRate = (wA + d * 0.5f) / TrialsPerCheck;

                if (Experiment == ExperimentMode.Baseline)
                {
                    // 採用判定なし。勝率を蓄積して表示するだけ
                    baselineWinSum += winRate;
                    baselineCount++;
                    if (baselineCount % 10 == 0)
                    {
                        float avg = baselineWinSum / baselineCount;
                        string tag = Mathf.Abs(avg - 0.5f) >= 0.03f ? "  ★ 偏りあり" : "";
                        Debug.Log($"[Baseline] {i + 1}/{Iterations}  winRate={winRate:F3}  avg={avg:F3}{tag}");
                    }
                }
                else if (winRate > 0.55f)
                {
                    bool hofOk = true; float hofRate = 1f;
                    yield return StartCoroutine(CheckHallOfFame(candidate, i,
                        (ok, rate) => { hofOk = ok; hofRate = rate; }));
                    if (hofOk)
                    {
                        _hallOfFame.Add(best.Clone());
                        if (_hallOfFame.Count > HallOfFameSize) _hallOfFame.RemoveAt(0);
                        best = candidate;
                        adopted++;
                        _logBuffer.Add(BuildLogLine(totalIter, winRate, best));
                        FlushLogBuffer(logFile);
                        SaveWeights(best, weightsFile);
                        string rejectStr = rejectRun > 0 ? $"  (却下×{rejectRun})" : "";
                        Debug.Log($"[Opt] {i + 1}/{Iterations}  採用#{adopted}  winRate={winRate:F2}  hofRate={hofRate:F2}{rejectStr}");
                        rejectRun = 0;
                    }
                    else
                    {
                        rejectRun++;
                        Debug.Log($"[Opt] {i + 1}/{Iterations}  HoF不合格  hofRate={hofRate:F2}");
                    }
                }
                else
                {
                    rejectRun++;
                    if (rejectRun % 20 == 0)
                        Debug.Log($"[Opt] {i + 1}/{Iterations}  却下×{rejectRun}件連続  採用率={adopted}/{i + 1}");
                }

                cyclePos++;
                if (cyclePos >= (int)curCycleLen)
                {
                    cyclePos = 0;
                    curCycleLen = Mathf.Max(curCycleLen * CycleMult, 2f);
                    string rejectStr = rejectRun > 0 ? $"  (却下×{rejectRun})" : "";
                    Debug.Log($"[Opt] ★ ウォームリスタート  次サイクル={curCycleLen}  scale→{ScaleMax:F1}{rejectStr}");
                }
            }

            if (Experiment == ExperimentMode.Baseline && baselineCount > 0)
            {
                float avg = baselineWinSum / baselineCount;
                float biasPct = (avg - 0.5f) * 100f;
                string verdict = Mathf.Abs(avg - 0.5f) < 0.03f
                    ? "公平（問題なし）"
                    : "★ 偏りあり（ゲームバランスを確認してください）";
                Debug.Log($"[Baseline] 完了  平均winRate={avg:F3}  ({biasPct:+0.0;-0.0}%)  {verdict}");
            }
            else
            {
                if (rejectRun > 0)
                    Debug.Log($"[Opt] 最終{rejectRun}件却下");

                FlushLogBuffer(logFile);
                SaveWeights(best, weightsFile);
                Debug.Log($"[Opt] Done  採用={adopted}/{Iterations}  採用率={(float)adopted / Iterations:P0}");
            }
        }

        // メモリ解放
        _bakedMaps.Clear();
        while (_boardPool.TryTake(out _)) { }
    }

    // ---- Warm-up：地形をまとめてベイク ----

    IEnumerator BakeMapsPhase()
    {
        _bakedMaps.Clear();

        var savedSeeds = SavedMapsManager.Seeds;
        bool usingSaved = savedSeeds.Count > 0;
        if (usingSaved)
            Debug.Log($"[Opt] 保存済みマップ {savedSeeds.Count}個 を使用して学習");
        else
            Debug.Log($"[Opt] 保存済みマップなし → ランダム生成で学習");

        // TerrainGenerator は純粋静的関数 → GridManager 不要でベイク可能
        TerrainGenerator.COLS = 7;
        TerrainGenerator.ROWS = 14;
        TerrainGenerator.BASE_ROWS = 2;
        TerrainGenerator.HOME_MIN_X = 1;
        TerrainGenerator.HOME_MAX_X = 5;

        // プール内の古いサイズの SimBoard を破棄（サイズ変更後に古いボードが混入するのを防ぐ）
        while (_boardPool.TryTake(out _)) { }

        _randomBaseSeed = usingSaved ? SIM_SEED : new System.Random().Next();
        Debug.Log($"[Opt] BakeMapCount={BakeMapCount}  BakeMapsPhase 開始 (seed base={_randomBaseSeed})");

        for (int i = 0; i < BakeMapCount; i++)
        {
            StatusText = $"Baking Maps... {i + 1}/{BakeMapCount}";

            int seed = usingSaved
                ? savedSeeds[i % savedSeeds.Count]
                : _randomBaseSeed + i * 97 + 1;

            TileCell[,] rawGrid;
            try { rawGrid = TerrainGenerator.Generate(seed); }
            catch (System.Exception e)
            {
                Debug.LogError($"[Opt] Generate({seed}) 失敗: {e}");
                yield break;
            }

            var tiles = BakeSimTiles(rawGrid);
            var lhList = new List<Vector2Int>();
            for (int x = 0; x < TerrainGenerator.COLS; x++)
                for (int z = 0; z < TerrainGenerator.ROWS; z++)
                    if (tiles[x, z].Outpost == OutpostType.Lighthouse)
                        lhList.Add(new Vector2Int(x, z));

            _bakedMaps.Add(new BakedMap { Tiles = tiles, Lighthouses = lhList.ToArray() });
            yield return null;
        }

        StatusText = "";
        Debug.Log($"[Opt] ベイク完了: {_bakedMaps.Count}/{BakeMapCount} maps");
    }

    // ---- Hall of Fame 評価 ----

    IEnumerator CheckHallOfFame(AIWeightSet candidate, int seedOffset,
                                System.Action<bool, float> onComplete)
    {
        if (_hallOfFame.Count == 0) { onComplete(true, 1f); yield break; }
        float hofWins = 0f;
        int   hofTotal = 0;
        for (int h = 0; h < _hallOfFame.Count; h++)
        {
            int hA = 0, hB = 0, hD = 0;
            yield return StartCoroutine(ParallelTrialLoop(candidate, _hallOfFame[h],
                HallOfFameTrials, seedOffset + h * 997,
                (a, b, d2) => { hA = a; hB = b; hD = d2; }));
            hofWins  += hA + hD * 0.5f;
            hofTotal += HallOfFameTrials;
        }
        float rate = hofWins / hofTotal;
        onComplete(rate > 0.5f, rate);
    }

    // ---- 並列試合ループ（キャッシュ参照版） ----

    IEnumerator ParallelTrialLoop(AIWeightSet candidate, AIWeightSet best,
                                  int trials, int seedOffset,
                                  System.Action<int, int, int> onComplete,
                                  AnimalType? forcedAnimal = null)
    {
        int mapCount = _bakedMaps.Count;
        if (mapCount == 0)
        {
            Debug.LogError("[Opt] ベイク済みマップがありません。BakeMapCount を確認してください。");
            onComplete(0, 0, trials);
            yield break;
        }

        // フェーズ1：メインスレッドでチーム選択（GenerateMap 呼び出しなし）
        var simData = new (SimTile[,] tiles, SimUnit[] allUnits, bool swapped)[trials];

        // ペアリング：(i=0,i=1),(i=2,i=3)... が同一マップで swap/非swap を試す
        int halfTrials = trials / 2;
        for (int i = 0; i < trials; i++)
        {
            bool swapped = i % 2 == 1;
            int pairIdx = i / 2;
            var map = _bakedMaps[(seedOffset * halfTrials + pairIdx) % mapCount];

            var wP1 = swapped ? best : candidate;
            var wP2 = swapped ? candidate : best;

            var teamA = SelectTeamSim(wP1, map.Tiles, map.Lighthouses, 1, forcedAnimal);
            var teamB = SelectTeamSim(wP2, map.Tiles, map.Lighthouses, 2, forcedAnimal);
            var unitsA = BuildSimUnits(teamA, 1);
            var unitsB = BuildSimUnits(teamB, 2);
            PlaceSimLoadout(unitsA, 1);
            PlaceSimLoadout(unitsB, 2);

            var all = new SimUnit[unitsA.Length + unitsB.Length];
            unitsA.CopyTo(all, 0);
            unitsB.CopyTo(all, unitsA.Length);

            simData[i] = (map.Tiles, all, swapped);
        }

        // フェーズ2：バックグラウンドで全試合を並列実行
        var results = new int[trials];
        Task task = null;

        yield return new WaitUntil(() =>
        {
            if (task == null)
            {
                var cap_c = candidate;
                var cap_b = best;
                task = Task.Run(() =>
                {
                    Parallel.For(0, trials, j =>
                    {
                        var (tiles, allUnits, swapped) = simData[j];
                        var wP1 = swapped ? cap_b : cap_c;
                        var wP2 = swapped ? cap_c : cap_b;

                        var board = RentBoard();
                        board.Reset(tiles, allUnits, allUnits.Length, wP1, wP2);
                        results[j] = board.RunToEnd(MAX_TICKS);
                        ReturnBoard(board);
                    });
                });
            }
            return task.IsCompleted;
        });

        if (task.IsFaulted)
            Debug.LogError("[Opt] 並列実行エラー: " + task.Exception?.Flatten().InnerException?.Message);

        // フェーズ3：swap 補正して集計
        int winsA = 0, winsB = 0, draws = 0;
        for (int i = 0; i < trials; i++)
        {
            int r = results[i];
            if (simData[i].swapped) { if (r == 1) r = 2; else if (r == 2) r = 1; }
            if (r == 1) winsA++;
            else if (r == 2) winsB++;
            else draws++;
        }

        onComplete(winsA, winsB, draws);
    }

    // ---- SimBoard ヘルパー ----

    static SimTile[,] BakeSimTiles(TileCell[,] grid)
    {
        int cols = grid.GetLength(0), rows = grid.GetLength(1);
        var result = new SimTile[cols, rows];
        for (int x = 0; x < cols; x++)
            for (int z = 0; z < rows; z++)
            {
                var cell = grid[x, z];
                result[x, z] = new SimTile
                {
                    Type = cell?.Type ?? TerrainType.Flat,
                    Outpost = cell?.Outpost ?? OutpostType.None,
                };
            }
        return result;
    }

    static SimUnit[] BuildSimUnits(AnimalType[] team, int owner)
    {
        var units = new SimUnit[team.Length];
        for (int i = 0; i < team.Length; i++)
        {
            var type = team[i];
            int hp = AnimalDefinitions.GetMaxHP(type);
            units[i] = new SimUnit
            {
                Id = owner * 100 + i,
                AnimalType = type,
                Owner = owner,
                CurrentHP = hp,
                MaxHP = hp,
                AttackPower = AnimalDefinitions.GetAttackPower(type),
            };
        }
        return units;
    }

    static void PlaceSimLoadout(SimUnit[] units, int owner)
    {
        int zBase = owner == 1 ? 0 : TerrainGenerator.ROWS - TerrainGenerator.BASE_ROWS;
        int placed = 0;
        for (int row = 0; row < TerrainGenerator.BASE_ROWS && placed < units.Length; row++)
            for (int x = TerrainGenerator.HOME_MIN_X; x <= TerrainGenerator.HOME_MAX_X && placed < units.Length; x++)
                units[placed++].Pos = new Vector2Int(x, zBase + row);
    }

    // ---- BFS チーム選択（SimTile[,] 版） ----

    static AnimalType[] SelectTeamSim(AIWeightSet w, SimTile[,] tiles,
                                      Vector2Int[] lighthouses, int owner,
                                      AnimalType? forcedAnimal = null)
    {
        int totalTiles = tiles.GetLength(0) * tiles.GetLength(1);
        var scored = new List<(float score, AnimalType type)>();

        foreach (var type in AllCandidates)
        {
            var (reachable, canReach) = CountReachableTilesSim(type, owner, tiles, lighthouses);
            float mobility = (float)reachable / totalTiles;
            float score = w.AnimalPick.Get(type) + w.TerrainPickScale * mobility;
            if (!canReach && lighthouses.Length > 0) score -= 50f;
            scored.Add((score, type));
        }
        scored.Sort((a, b) => b.score.CompareTo(a.score));

        int budget = 130 - AnimalDefinitions.GetCost(AnimalType.Fox);
        int slots = 9;
        var team = new List<AnimalType> { AnimalType.Fox };

        // AnimalWeights 学習時：テスト対象を必ずチームに含める
        if (forcedAnimal.HasValue && forcedAnimal.Value != AnimalType.Fox)
        {
            int cost = AnimalDefinitions.GetCost(forcedAnimal.Value);
            if (cost <= budget) { team.Add(forcedAnimal.Value); budget -= cost; slots--; }
        }

        foreach (var (_, type) in scored)
        {
            if (slots <= 0 || budget <= 0) break;
            if (forcedAnimal.HasValue && type == forcedAnimal.Value) continue; // 強制選出済み
            int cost = AnimalDefinitions.GetCost(type);
            if (cost <= budget) { team.Add(type); budget -= cost; slots--; }
        }

        return team.ToArray();
    }

    static (int reachable, bool canReachLighthouse) CountReachableTilesSim(
        AnimalType type, int owner, SimTile[,] tiles, Vector2Int[] lighthouses)
    {
        int cols = tiles.GetLength(0), rows = tiles.GetLength(1);
        var blocked = AnimalDefinitions.GetBlockedTerrain(type);
        var offsets = AnimalDefinitions.GetMoveOffsets(type);
        int fSign = owner == 1 ? 1 : -1;
        var visited = new HashSet<Vector2Int>();
        var queue = new Queue<Vector2Int>();

        int zBase = owner == 1 ? 0 : rows - TerrainGenerator.BASE_ROWS;
        for (int x = TerrainGenerator.HOME_MIN_X; x <= TerrainGenerator.HOME_MAX_X; x++)
            for (int dz = 0; dz < TerrainGenerator.BASE_ROWS; dz++)
            {
                var pos = new Vector2Int(x, zBase + dz);
                var t = tiles[pos.x, pos.y];
                if (t.Type != TerrainType.Bridge && blocked.Contains(t.Type)) continue;
                if (visited.Add(pos)) queue.Enqueue(pos);
            }

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var raw in offsets)
            {
                var off = new Vector2Int(raw.x, raw.y * fSign);
                var next = cur + off;
                if ((uint)next.x >= (uint)cols || (uint)next.y >= (uint)rows) continue;
                if (visited.Contains(next)) continue;
                var t = tiles[next.x, next.y];
                if (t.Type != TerrainType.Bridge && blocked.Contains(t.Type)) continue;
                if (Math.Abs(off.x) == 2 || Math.Abs(off.y) == 2)
                {
                    var mid = cur + new Vector2Int(off.x / 2, off.y / 2);
                    if ((uint)mid.x >= (uint)cols || (uint)mid.y >= (uint)rows) continue;
                    var mt = tiles[mid.x, mid.y];
                    if (mt.Type != TerrainType.Bridge && blocked.Contains(mt.Type)) continue;
                }
                visited.Add(next);
                queue.Enqueue(next);
            }
        }

        bool canReach = false;
        if (lighthouses != null)
            foreach (var lh in lighthouses)
                if (visited.Contains(lh)) { canReach = true; break; }

        return (visited.Count, canReach);
    }

    // ---- ファイル I/O ----

    static void SaveWeights(AIWeightSet w, string filename)
    {
        Debug.Log($"[Opt] Before Save  NumericalPowerMult={w.NumericalPowerMult:F3}");
        string path = Path.Combine(Application.persistentDataPath, filename);
        File.WriteAllText(path, JsonUtility.ToJson(w, prettyPrint: true));

        Debug.Log($"[Opt] Saved → {path}");
    }

    static AIWeightSet LoadWeights(string filename)
    {
        string path = Path.Combine(Application.persistentDataPath, filename);
        if (!File.Exists(path)) return null;
        var w = JsonUtility.FromJson<AIWeightSet>(File.ReadAllText(path));
        Debug.Log($"[Opt] Loaded → {path}");
        Debug.Log($"[Opt] After Load   NumericalPowerMult={w.NumericalPowerMult:F3}");
        return w;
    }

    // ---- ログ ----

    static readonly (string label, System.Func<AIWeightSet, AIWeights> get)[] Groups =
    {
        ("Tiger",      s => s.Tiger),      ("Bear",       s => s.Bear),
        ("Gorilla",    s => s.Gorilla),    ("Lion",       s => s.Lion),
        ("Chimpanzee", s => s.Chimpanzee), ("Rhino",      s => s.Rhino),
        ("Horse",      s => s.Horse),      ("Snake",      s => s.Snake),
        ("Boar",       s => s.Boar),       ("Hippo",      s => s.Hippo),
        ("Giraffe",    s => s.Giraffe),    ("Elephant",   s => s.Elephant),
        ("Panda",      s => s.Panda),      ("Wolf",       s => s.Wolf),
        ("Reindeer",   s => s.Reindeer),   ("Fox",        s => s.Fox),
    };

    static void EnsureLogHeader(string filename)
    {
        string path = Path.Combine(Application.persistentDataPath, filename);
        if (File.Exists(path)) return;
        var sb = new StringBuilder();
        sb.Append("iter,winRate");
        foreach (var (label, _) in Groups)
            foreach (var f in AIWeightsFields)
                sb.Append($",{label}_{f.Name}");
        sb.Append(",TerrainBonus");
        foreach (var f in AnimalPickFields)
            sb.Append($",AnimalPick_{f.Name}");
        sb.Append(",TerrainPickScale");
        sb.AppendLine();
        File.WriteAllText(path, sb.ToString());
        Debug.Log($"[Opt] Log → {path}");
    }

    static string BuildLogLine(int iter, float winRate, AIWeightSet w)
    {
        var sb = new StringBuilder();
        sb.Append($"{iter},{winRate:F3}");
        foreach (var (_, get) in Groups)
            foreach (var f in AIWeightsFields)
                sb.Append($",{(float)f.GetValue(get(w)):F2}");
        sb.Append($",{w.TerrainBonus:F2}");
        foreach (var f in AnimalPickFields)
            sb.Append($",{(float)f.GetValue(w.AnimalPick):F2}");
        sb.Append($",{w.TerrainPickScale:F2}");
        return sb.ToString();
    }

    void FlushLogBuffer(string filename)
    {
        if (_logBuffer.Count == 0) return;
        string path = Path.Combine(Application.persistentDataPath, filename);
        try { File.AppendAllLines(path, _logBuffer); }
        catch (System.Exception e) { Debug.LogWarning($"[Opt] ログ書き込み失敗: {e.Message}"); }
        _logBuffer.Clear();
    }

    static void TrimLog(string filename)
    {
        string path = Path.Combine(Application.persistentDataPath, filename);
        if (!File.Exists(path)) return;
        var lines = File.ReadAllLines(path);
        if (lines.Length <= MAX_LOG_LINES + 1) return;
        var trimmed = new string[MAX_LOG_LINES + 1];
        trimmed[0] = lines[0]; // ヘッダー行を保持
        System.Array.Copy(lines, lines.Length - MAX_LOG_LINES, trimmed, 1, MAX_LOG_LINES);
        File.WriteAllLines(path, trimmed);
        Debug.Log($"[Opt] ログを {MAX_LOG_LINES} 行に圧縮");
    }

    static int GetLogLineCount(string filename)
    {
        string path = Path.Combine(Application.persistentDataPath, filename);
        if (!File.Exists(path)) return 0;
        int count = 0;
        using var sr = new StreamReader(path);
        while (sr.ReadLine() != null) count++;
        return Mathf.Max(0, count - 1);
    }
}
#endif
