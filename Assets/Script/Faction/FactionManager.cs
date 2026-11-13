using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 월드 전체를 아는 유일한 관리자. 씬에 하나만 둔다.
///  - 진영(FactionCamp)들을 매 프레임 업데이트 → 진영이 자기 몬스터를 업데이트
///  - 살아 있는 FactionBody(몬스터 + 플레이어)를 공간 그리드에 넣어 주변 탐색을 가볍게 유지
///  - 진영 간 적대 판정, 플레이어 거리 (진영 휴면/리스폰 판단용)
/// </summary>
[DefaultExecutionOrder(-100)]
public class FactionManager : MonoBehaviour
{
    public static FactionManager Instance { get; private set; }

    [System.Serializable]
    public struct Rivalry
    {
        public Faction a;
        public Faction b;
    }

    [Header("Spatial Grid")]
    [Tooltip("몬스터의 neighborRadius와 비슷하게")]
    public float cellSize = 6f;

    [Header("Diplomacy")]
    [Tooltip("플레이어는 모든 몬스터 진영과 적. 몬스터 진영끼리는 여기 넣은 쌍만 서로 싸움")]
    public List<Rivalry> rivalries = new List<Rivalry>();

    [Header("Player")]
    [Tooltip("비워두면 'Player' 태그로 찾음. 진영 휴면/리스폰 거리 계산에 씀")]
    public Transform player;

    readonly List<FactionCamp> camps = new List<FactionCamp>();
    readonly List<FactionBody> bodies = new List<FactionBody>();
    readonly Dictionary<Vector2Int, List<FactionBody>> grid = new Dictionary<Vector2Int, List<FactionBody>>();

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        if (player == null)
        {
            var p = GameObject.FindWithTag("Player");
            if (p != null) player = p.transform;
        }
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void RegisterCamp(FactionCamp c) { if (!camps.Contains(c)) camps.Add(c); }
    public void UnregisterCamp(FactionCamp c) { camps.Remove(c); }
    public void RegisterBody(FactionBody b) { if (!bodies.Contains(b)) bodies.Add(b); }
    public void UnregisterBody(FactionBody b) { bodies.Remove(b); }

    void Update()
    {
        float dt = Time.deltaTime;
        RebuildGrid();

        for (int i = camps.Count - 1; i >= 0; i--)
        {
            var c = camps[i];
            if (c != null && c.isActiveAndEnabled) c.Tick(dt);
        }
    }

    public bool IsHostile(Faction a, Faction b)
    {
        if (a == b) return false;
        if (a == Faction.Player || b == Faction.Player) return true;
        foreach (var r in rivalries)
            if ((r.a == a && r.b == b) || (r.a == b && r.b == a)) return true;
        return false;
    }

    /// <summary>플레이어까지의 수평 거리. 플레이어가 없으면 0 (= 항상 가까운 것으로 취급)</summary>
    public float DistanceToPlayer(Vector3 p)
    {
        if (player == null) return 0f;
        Vector3 d = player.position - p;
        d.y = 0f;
        return d.magnitude;
    }

    public Color ColorOf(Faction f)
    {
        switch (f)
        {
            case Faction.Goblin: return new Color(0.35f, 0.75f, 0.3f);
            case Faction.Orc: return new Color(0.85f, 0.3f, 0.2f);
            case Faction.Undead: return new Color(0.6f, 0.55f, 0.85f);
            case Faction.Beast: return new Color(0.75f, 0.55f, 0.3f);
            default: return Color.white;
        }
    }

    Vector2Int Cell(Vector3 p) =>
        new Vector2Int(Mathf.FloorToInt(p.x / cellSize), Mathf.FloorToInt(p.z / cellSize));

    void RebuildGrid()
    {
        // 넓은 오픈월드에서 빈 칸이 계속 쌓이지 않도록 가끔 통째로 비움
        if (grid.Count > bodies.Count * 4 + 64) grid.Clear();
        else foreach (var list in grid.Values) list.Clear();

        foreach (var b in bodies)
        {
            if (b == null || !b.isActiveAndEnabled || !b.IsAlive) continue;
            var key = Cell(b.Position);
            if (!grid.TryGetValue(key, out var list))
            {
                list = new List<FactionBody>();
                grid[key] = list;
            }
            list.Add(b);
        }
    }

    /// <summary>반경 안의 살아 있는 몸체를 진영 상관없이 모두 담는다 (self 제외)</summary>
    public void GetNearby(Vector3 pos, float radius, List<FactionBody> results, FactionBody self)
    {
        results.Clear();
        float r2 = radius * radius;
        int range = Mathf.CeilToInt(radius / cellSize);
        var c = Cell(pos);

        for (int x = -range; x <= range; x++)
            for (int z = -range; z <= range; z++)
            {
                if (!grid.TryGetValue(new Vector2Int(c.x + x, c.y + z), out var list)) continue;
                foreach (var b in list)
                {
                    if (b == self || !b.IsAlive) continue; // 이번 프레임에 죽은 대상 제외
                    Vector3 d = b.Position - pos;
                    d.y = 0f;
                    if (d.sqrMagnitude <= r2) results.Add(b);
                }
            }
    }
}
