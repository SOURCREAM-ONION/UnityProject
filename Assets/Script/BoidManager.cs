using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 모든 BoidMonster를 한 곳에서 업데이트한다.
/// 1) 공간 그리드: 이웃 탐색을 주변 몇 칸으로 제한해 몬스터 수가 많아도 가볍게 유지
/// 2) 공격 토큰: 한 타깃에게 동시에 달려드는 몬스터 수를 제한
/// 씬에 하나만 둔다.
/// </summary>
[DefaultExecutionOrder(-100)]
public class BoidManager : MonoBehaviour
{
    public static BoidManager Instance { get; private set; }

    [Header("Spatial Grid")]
    [Tooltip("몬스터의 neighborRadius와 비슷하게")]
    public float cellSize = 6f;

    [Header("Attack Tokens")]
    [Tooltip("한 타깃에게 동시에 달려들 수 있는 몬스터 수")]
    public int maxAttackersPerTarget = 2;

    readonly List<BoidMonster> agents = new List<BoidMonster>();
    readonly Dictionary<Vector2Int, List<BoidMonster>> grid = new Dictionary<Vector2Int, List<BoidMonster>>();
    readonly Dictionary<Transform, HashSet<BoidMonster>> tokens = new Dictionary<Transform, HashSet<BoidMonster>>();

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void Register(BoidMonster a) { if (!agents.Contains(a)) agents.Add(a); }
    public void Unregister(BoidMonster a) { agents.Remove(a); }

    void Update()
    {
        float dt = Time.deltaTime;
        RebuildGrid();

        for (int i = agents.Count - 1; i >= 0; i--)
        {
            var a = agents[i];
            if (a != null && a.isActiveAndEnabled) a.Tick(dt);
        }
    }

    // ───────── 공간 그리드 ─────────

    Vector2Int Cell(Vector3 p) =>
        new Vector2Int(Mathf.FloorToInt(p.x / cellSize), Mathf.FloorToInt(p.z / cellSize));

    void RebuildGrid()
    {
        // 넓은 오픈월드에서 빈 칸이 계속 쌓이지 않도록 가끔 통째로 비움
        if (grid.Count > agents.Count * 4 + 64) grid.Clear();
        else foreach (var list in grid.Values) list.Clear();

        foreach (var a in agents)
        {
            if (a == null || !a.isActiveAndEnabled) continue;
            var key = Cell(a.transform.position);
            if (!grid.TryGetValue(key, out var list))
            {
                list = new List<BoidMonster>();
                grid[key] = list;
            }
            list.Add(a);
        }
    }

    public void GetNeighbors(Vector3 pos, float radius, List<BoidMonster> results, BoidMonster self)
    {
        results.Clear();
        float r2 = radius * radius;
        int range = Mathf.CeilToInt(radius / cellSize);
        var c = Cell(pos);

        for (int x = -range; x <= range; x++)
            for (int z = -range; z <= range; z++)
            {
                if (!grid.TryGetValue(new Vector2Int(c.x + x, c.y + z), out var list)) continue;
                foreach (var a in list)
                {
                    if (a == self) continue;
                    Vector3 d = a.transform.position - pos;
                    d.y = 0f;
                    if (d.sqrMagnitude <= r2) results.Add(a);
                }
            }
    }

    // ───────── 공격 토큰 ─────────

    public bool TryAcquireAttackToken(Transform target, BoidMonster a)
    {
        if (target == null) return false;
        if (!tokens.TryGetValue(target, out var set))
        {
            set = new HashSet<BoidMonster>();
            tokens[target] = set;
        }
        set.RemoveWhere(m => m == null);
        if (set.Contains(a)) return true;
        if (set.Count >= maxAttackersPerTarget) return false;
        set.Add(a);
        return true;
    }

    public void ReleaseAttackToken(Transform target, BoidMonster a)
    {
        if ((object)target == null) return;
        if (tokens.TryGetValue(target, out var set)) set.Remove(a);
    }
}