using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 진영전 관리자. 씬에 하나만 둔다.
///  - 모든 FactionUnit을 한 곳에서 업데이트
///  - 살아 있는 FactionBody(몬스터 + 플레이어)를 공간 그리드에 넣어 주변 탐색을 가볍게 유지
///  - 진영 간 적대/동맹 판정
/// </summary>
[DefaultExecutionOrder(-100)]
public class FactionManager : MonoBehaviour
{
    public static FactionManager Instance { get; private set; }

    [System.Serializable]
    public struct Alliance
    {
        public Faction a;
        public Faction b;
    }

    [Header("Spatial Grid")]
    [Tooltip("유닛의 neighborRadius와 비슷하게")]
    public float cellSize = 6f;

    [Header("Diplomacy")]
    [Tooltip("여기 넣은 쌍은 서로 공격하지 않음. 나머지 다른 진영끼리는 모두 적")]
    public List<Alliance> alliances = new List<Alliance>();

    readonly List<FactionUnit> units = new List<FactionUnit>();
    readonly List<FactionBody> bodies = new List<FactionBody>();
    readonly Dictionary<Vector2Int, List<FactionBody>> grid = new Dictionary<Vector2Int, List<FactionBody>>();

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void RegisterUnit(FactionUnit u) { if (!units.Contains(u)) units.Add(u); }
    public void UnregisterUnit(FactionUnit u) { units.Remove(u); }
    public void RegisterBody(FactionBody b) { if (!bodies.Contains(b)) bodies.Add(b); }
    public void UnregisterBody(FactionBody b) { bodies.Remove(b); }

    void Update()
    {
        float dt = Time.deltaTime;
        RebuildGrid();

        // Tick 도중 죽은 유닛이 Destroy되며 리스트에서 빠질 수 있어서 뒤에서부터 돈다
        for (int i = units.Count - 1; i >= 0; i--)
        {
            if (i >= units.Count) continue;
            var u = units[i];
            if (u != null && u.isActiveAndEnabled) u.Tick(dt);
        }
    }

    public bool IsHostile(Faction a, Faction b)
    {
        if (a == b) return false;
        foreach (var al in alliances)
            if ((al.a == a && al.b == b) || (al.a == b && al.b == a)) return false;
        return true;
    }

    public Color ColorOf(Faction f)
    {
        switch (f)
        {
            case Faction.Red: return new Color(0.9f, 0.25f, 0.2f);
            case Faction.Blue: return new Color(0.2f, 0.45f, 0.95f);
            case Faction.Green: return new Color(0.3f, 0.8f, 0.3f);
            case Faction.Yellow: return new Color(0.95f, 0.8f, 0.2f);
            default: return Color.white;
        }
    }

    Vector2Int Cell(Vector3 p) =>
        new Vector2Int(Mathf.FloorToInt(p.x / cellSize), Mathf.FloorToInt(p.z / cellSize));

    void RebuildGrid()
    {
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
