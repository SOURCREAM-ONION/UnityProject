using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

public enum CampState
{
    [Tooltip("플레이어가 멀리 있음. 몬스터를 재우고 업데이트하지 않음")]
    Dormant,
    [Tooltip("평온. 경비는 자리를 지키고 배회조는 순찰")]
    Calm,
    [Tooltip("수상한 기척. 몇 마리가 조사하러 감")]
    Alert,
    [Tooltip("적 확인. 진영 전체가 전투")]
    Combat,
    [Tooltip("전멸. 리스폰 대기")]
    Cleared
}

/// <summary>
/// 진영(야영지). 소속 몬스터를 생성하고, "집단으로서의 상황 판단"을 맡는다.
/// 몬스터 개인은 자기 눈에 보인 것을 진영에 보고만 하고, 진영이 상태를 정해 전원에게 알린다.
///
///  Dormant ──(플레이어 접근)──▶ Calm ──(수상함)──▶ Alert ──(확인)──▶ Combat
///     ▲                          ▲ ◀──(조사 끝)────┘ ◀──(놓침)───────┘ │
///     └──(플레이어 멀어짐)──────┘ ◀──(영역 밖으로 도주 / 적 사망)───────┘
///  Cleared ──(리스폰 시간 + 플레이어가 멀리 있음)──▶ Calm/Dormant
/// </summary>
public class FactionCamp : MonoBehaviour
{
    [System.Serializable]
    public class SpawnEntry
    {
        public FactionUnit prefab;
        public int count = 4;
        public FactionUnit.Role role = FactionUnit.Role.Wanderer;
    }

    class KnownEnemy
    {
        public FactionBody body;
        public Vector3 lastPos;
        public float lastSeen;
    }

    [Header("Identity")]
    public Faction faction = Faction.Goblin;
    public string campName = "고블린 야영지";

    [Header("Spawn")]
    public List<SpawnEntry> spawns = new List<SpawnEntry>();
    public float spawnRadius = 6f;
    [Tooltip("경비병(Guard)이 설 자리. 위치와 바라보는 방향을 씀. 모자라면 남는 경비는 무작위 위치")]
    public Transform[] guardPosts;
    [Tooltip("배회조(Wanderer)가 무리 지어 도는 순찰 경로. 비워두면 진영 안에서 어슬렁거림")]
    public Transform[] patrolRoute;
    [Tooltip("순찰 지점마다 머무는 시간")]
    public float patrolWaitTime = 3f;

    [Header("Territory")]
    [Tooltip("평소 머무는 범위")]
    public float homeRadius = 12f;
    [Tooltip("적이 진영 중심에서 이 거리 밖으로 나가면 추격을 포기하고 복귀")]
    public float leashRadius = 35f;

    [Header("Awareness")]
    [Tooltip("수상한 기척을 조사하러 가는 마릿수")]
    public int investigatorCount = 2;
    [Tooltip("경계 상태가 이만큼 지속되면 다시 평온")]
    public float alertDuration = 10f;
    [Tooltip("전투 중 아무도 적을 못 본 채 이 시간이 지나면 놓친 것으로 판단 → 마지막 위치 수색")]
    public float forgetTime = 5f;

    [Header("Combat")]
    [Tooltip("적 하나에 동시에 붙어서 때릴 수 있는 수 (공격 슬롯). 나머지는 주위를 돌며 순번을 기다림")]
    public int maxAttackersPerTarget = 3;

    [Header("Open World")]
    [Tooltip("플레이어가 이 거리 안에 오면 진영이 깨어남")]
    public float wakeDistance = 60f;
    [Tooltip("플레이어가 이 거리 밖이고 평온 상태면 진영이 잠듦 (wakeDistance보다 크게)")]
    public float sleepDistance = 80f;
    [Tooltip("잠들 때 몬스터 오브젝트를 비활성화 (렌더링/물리 비용까지 0)")]
    public bool deactivateWhenDormant = true;

    [Header("Respawn")]
    [Tooltip("전멸 후 다시 채워지기까지 시간 (초). 음수면 리스폰 안 함")]
    public float respawnTime = 120f;
    [Tooltip("플레이어가 보는 앞에서 생겨나지 않도록, 이 거리 밖에 있을 때만 리스폰")]
    public float respawnMinDistance = 50f;

    [Header("Events (퀘스트, 보상, 사운드 연결용)")]
    public UnityEvent onCombatStart;
    public UnityEvent onCleared;
    public UnityEvent onRespawn;

    public CampState State { get; private set; } = CampState.Calm;
    public Vector3 Center => transform.position;
    public Vector3 InvestigatePoint { get; private set; }
    public IReadOnlyList<FactionUnit> Members => members;

    readonly List<FactionUnit> members = new List<FactionUnit>();
    readonly List<KnownEnemy> known = new List<KnownEnemy>();
    float stateTimer;
    int routeIndex;
    float routeWaitTimer;

    public int AliveCount
    {
        get
        {
            int n = 0;
            foreach (var m in members) if (m != null && m.Body.IsAlive) n++;
            return n;
        }
    }

    // ───────── 생명주기 ─────────

    void Start()
    {
        if (FactionManager.Instance != null) FactionManager.Instance.RegisterCamp(this);
        else Debug.LogWarning("씬에 FactionManager가 없습니다.", this);

        Spawn();
        if (DistanceToPlayer() > wakeDistance) SetState(CampState.Dormant);
    }

    void OnDestroy()
    {
        if (FactionManager.Instance != null) FactionManager.Instance.UnregisterCamp(this);
    }

    float DistanceToPlayer() =>
        FactionManager.Instance != null ? FactionManager.Instance.DistanceToPlayer(Center) : 0f;

    /// <summary>FactionManager가 매 프레임 호출</summary>
    public void Tick(float dt)
    {
        stateTimer += dt;
        float playerDist = DistanceToPlayer();

        switch (State)
        {
            case CampState.Dormant:
                if (playerDist < wakeDistance) SetState(CampState.Calm);
                return; // 자는 동안은 몬스터를 전혀 업데이트하지 않음

            case CampState.Cleared:
                if (respawnTime >= 0f && stateTimer >= respawnTime && playerDist > respawnMinDistance)
                {
                    Respawn();
                    SetState(playerDist < wakeDistance ? CampState.Calm : CampState.Dormant);
                    return;
                }
                break;

            case CampState.Calm:
                if (playerDist > sleepDistance && AllSettled()) { SetState(CampState.Dormant); return; }
                UpdatePatrol(dt);
                break;

            case CampState.Alert:
                if (stateTimer >= alertDuration) SetState(CampState.Calm);
                break;

            case CampState.Combat:
                UpdateKnownEnemies();
                break;
        }

        for (int i = members.Count - 1; i >= 0; i--)
        {
            if (i >= members.Count) continue;
            var m = members[i];
            if (m != null && m.isActiveAndEnabled) m.Tick(dt);
        }

        if (State != CampState.Cleared && members.Count > 0 && AliveCount == 0) SetState(CampState.Cleared);
    }

    // ───────── 진영 상태 전이 ─────────

    void SetState(CampState next)
    {
        if (next == State) return;
        CampState prev = State;
        State = next;
        stateTimer = 0f;

        if (prev == CampState.Dormant) SetMembersActive(true);

        switch (next)
        {
            case CampState.Dormant:
                // 잠들기 전에 정리: 시체 제거, 전원 제자리 + 체력 회복 (돌아왔을 때 항상 같은 모습)
                RemoveDead();
                known.Clear();
                foreach (var m in members) if (m != null) m.ResetToPost();
                if (deactivateWhenDormant) SetMembersActive(false);
                break;

            case CampState.Calm:
                known.Clear();
                foreach (var m in members) if (m != null) m.OnCampCalm();
                break;

            case CampState.Alert:
                AssignInvestigators();
                break;

            case CampState.Combat:
                onCombatStart?.Invoke();
                foreach (var m in members) if (m != null) m.OnCampCombat();
                break;

            case CampState.Cleared:
                known.Clear();
                onCleared?.Invoke();
                break;
        }
    }

    // ───────── 몬스터 → 진영 보고 ─────────

    /// <summary>"뭔가 있는 것 같다" — 평온하면 경계로 바꾸고 조사조를 보낸다</summary>
    public void ReportSuspicion(Vector3 pos)
    {
        if (IsOutsideLeash(pos)) return;

        if (State == CampState.Calm)
        {
            InvestigatePoint = pos;
            SetState(CampState.Alert);
        }
        else if (State == CampState.Alert)
        {
            InvestigatePoint = pos; // 조사조는 매 프레임 이 지점을 읽음
            stateTimer = 0f;
        }
    }

    /// <summary>"적이다!" — 진영 전체 전투. 전투 중이면 그 적의 마지막 위치를 갱신</summary>
    public void ReportSighting(FactionBody enemy)
    {
        if (enemy == null || !enemy.IsAlive || IsOutsideLeash(enemy.Position)) return;
        if (State == CampState.Dormant || State == CampState.Cleared) return;

        KnownEnemy k = null;
        foreach (var e in known) if (e.body == enemy) { k = e; break; }
        if (k == null)
        {
            k = new KnownEnemy { body = enemy };
            known.Add(k);
        }
        k.lastPos = enemy.Position;
        k.lastSeen = Time.time;

        if (State != CampState.Combat) SetState(CampState.Combat);
    }

    // ───────── 진영 → 몬스터 정보 ─────────

    /// <summary>
    /// 진영이 알고 있는 적 중 가장 좋은 표적.
    /// 점수 = 거리 + 이미 붙은 수 × crowdPenalty - (현재 표적이면 보너스) → 표적이 자연스럽게 분산됨
    /// </summary>
    public FactionBody BestTarget(Vector3 from, float crowdPenalty, FactionBody current)
    {
        FactionBody best = null;
        float bestScore = float.MaxValue;
        foreach (var k in known)
        {
            if (k.body == null || !k.body.IsAlive) continue;
            Vector3 d = k.body.Position - from;
            d.y = 0f;
            float score = d.magnitude + k.body.attackers * crowdPenalty - (k.body == current ? 2f : 0f);
            if (score < bestScore) { bestScore = score; best = k.body; }
        }
        return best;
    }

    public bool HasFreeSlot(FactionBody target) => target.attackers < maxAttackersPerTarget;

    /// <summary>배회조가 지금 향할 순찰 지점. 경로가 없으면 null</summary>
    public Vector3? GroupWaypoint
    {
        get
        {
            if (patrolRoute == null || patrolRoute.Length == 0) return null;
            var t = patrolRoute[routeIndex % patrolRoute.Length];
            return t != null ? t.position : (Vector3?)null;
        }
    }

    // ───────── 내부 처리 ─────────

    void UpdateKnownEnemies()
    {
        bool lostSight = false;
        Vector3 lostPos = Center;

        for (int i = known.Count - 1; i >= 0; i--)
        {
            var k = known[i];
            if (k.body == null || !k.body.IsAlive || IsOutsideLeash(k.body.Position))
            {
                known.RemoveAt(i);
            }
            else if (Time.time - k.lastSeen > forgetTime)
            {
                lostSight = true;
                lostPos = k.lastPos;
                known.RemoveAt(i);
            }
        }

        if (known.Count > 0) return;

        if (lostSight)
        {
            // 시야에서 사라졌을 뿐이면 마지막으로 본 곳을 수색
            InvestigatePoint = lostPos;
            SetState(CampState.Alert);
        }
        else
        {
            // 영역 밖으로 도망갔거나 쓰러뜨렸으면 그냥 복귀
            SetState(CampState.Calm);
        }
    }

    /// <summary>조사 지점에서 가까운 몇 마리만 보내고, 싸우던 나머지는 복귀시킨다</summary>
    void AssignInvestigators()
    {
        var candidates = new List<FactionUnit>();
        foreach (var m in members)
            if (m != null && m.Body.IsAlive && m.CanInvestigate) candidates.Add(m);

        Vector3 p = InvestigatePoint;
        candidates.Sort((a, b) =>
            (a.transform.position - p).sqrMagnitude.CompareTo((b.transform.position - p).sqrMagnitude));

        for (int i = 0; i < candidates.Count; i++)
        {
            if (i < investigatorCount) candidates[i].BeginInvestigate();
            else candidates[i].StandDown();
        }
    }

    void UpdatePatrol(float dt)
    {
        var wp = GroupWaypoint;
        if (!wp.HasValue) return;

        // 배회조 무리의 중심이 지점에 닿으면 잠시 쉬었다가 다음 지점으로
        Vector3 sum = Vector3.zero;
        int n = 0;
        foreach (var m in members)
        {
            if (m == null || !m.Body.IsAlive || m.role != FactionUnit.Role.Wanderer || !m.IsIdle) continue;
            sum += m.transform.position;
            n++;
        }
        if (n == 0) return;

        Vector3 d = sum / n - wp.Value;
        d.y = 0f;
        if (d.magnitude > 3f) { routeWaitTimer = 0f; return; }

        routeWaitTimer += dt;
        if (routeWaitTimer >= patrolWaitTime)
        {
            routeWaitTimer = 0f;
            routeIndex = (routeIndex + 1) % patrolRoute.Length;
        }
    }

    bool AllSettled()
    {
        foreach (var m in members)
            if (m != null && m.Body.IsAlive && !m.IsIdle) return false;
        return true;
    }

    void Spawn()
    {
        int guardIndex = 0;
        foreach (var e in spawns)
        {
            if (e.prefab == null) continue;
            for (int i = 0; i < e.count; i++)
            {
                Vector3 p;
                Quaternion rot;
                if (e.role == FactionUnit.Role.Guard && guardPosts != null && guardIndex < guardPosts.Length
                    && guardPosts[guardIndex] != null)
                {
                    p = guardPosts[guardIndex].position;
                    rot = Quaternion.Euler(0f, guardPosts[guardIndex].eulerAngles.y, 0f);
                    guardIndex++;
                }
                else
                {
                    Vector2 r = Random.insideUnitCircle * spawnRadius;
                    p = SnapToGround(Center + new Vector3(r.x, 0f, r.y), e.prefab.groundMask);
                    rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                }

                var m = Instantiate(e.prefab, p, rot, transform);
                m.camp = this;
                m.role = e.role;
                m.Body.faction = faction;
                m.SetPost(p, rot);
                AddMember(m);
            }
        }
    }

    void Respawn()
    {
        foreach (var m in members) if (m != null) Destroy(m.gameObject);
        members.Clear();
        routeIndex = 0;
        Spawn();
        onRespawn?.Invoke();
    }

    public void AddMember(FactionUnit m) { if (!members.Contains(m)) members.Add(m); }
    public void RemoveMember(FactionUnit m) { members.Remove(m); }

    void RemoveDead()
    {
        for (int i = members.Count - 1; i >= 0; i--)
        {
            var m = members[i];
            if (m == null) { members.RemoveAt(i); continue; }
            if (!m.Body.IsAlive) { members.RemoveAt(i); Destroy(m.gameObject); }
        }
    }

    void SetMembersActive(bool on)
    {
        foreach (var m in members) if (m != null) m.gameObject.SetActive(on);
    }

    static Vector3 SnapToGround(Vector3 p, LayerMask mask)
    {
        if (Physics.Raycast(p + Vector3.up * 20f, Vector3.down, out RaycastHit hit, 60f, mask,
                            QueryTriggerInteraction.Ignore))
            p.y = hit.point.y;
        return p;
    }

    public bool IsInHome(Vector3 p, float ratio = 1f)
    {
        Vector3 d = p - Center;
        d.y = 0f;
        float r = homeRadius * ratio;
        return d.sqrMagnitude <= r * r;
    }

    public bool IsOutsideLeash(Vector3 p)
    {
        Vector3 d = p - Center;
        d.y = 0f;
        return d.sqrMagnitude > leashRadius * leashRadius;
    }

    // ───────── 디버그 ─────────

    void OnDrawGizmos()
    {
        switch (State)
        {
            case CampState.Dormant: Gizmos.color = Color.gray; break;
            case CampState.Alert: Gizmos.color = Color.yellow; break;
            case CampState.Combat: Gizmos.color = Color.red; break;
            case CampState.Cleared: Gizmos.color = Color.black; break;
            default: Gizmos.color = Color.green; break;
        }
        Gizmos.DrawWireSphere(transform.position, homeRadius);
        Gizmos.DrawCube(transform.position + Vector3.up * 3f, new Vector3(0.3f, 6f, 0.3f)); // 깃발

        if (State == CampState.Alert) Gizmos.DrawWireSphere(InvestigatePoint, 1f);

        Gizmos.color = new Color(0f, 0.8f, 1f, 0.8f);
        if (guardPosts != null)
            foreach (var g in guardPosts)
                if (g != null)
                {
                    Gizmos.DrawWireCube(g.position + Vector3.up * 0.5f, Vector3.one);
                    Gizmos.DrawLine(g.position + Vector3.up * 0.5f, g.position + Vector3.up * 0.5f + g.forward * 1.5f);
                }

        if (patrolRoute != null && patrolRoute.Length > 1)
            for (int i = 0; i < patrolRoute.Length; i++)
            {
                var a = patrolRoute[i];
                var b = patrolRoute[(i + 1) % patrolRoute.Length];
                if (a != null && b != null) Gizmos.DrawLine(a.position + Vector3.up * 0.2f, b.position + Vector3.up * 0.2f);
            }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0f, 0f, 0.5f);
        Gizmos.DrawWireSphere(transform.position, leashRadius);
        Gizmos.color = new Color(0.5f, 0.5f, 0.5f, 0.3f);
        Gizmos.DrawWireSphere(transform.position, wakeDistance);
    }
}
