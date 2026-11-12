using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 진영형 몬스터: 보이드 조향 + 클래스 기반 FSM.
/// "무엇을 할지"는 상태(FactionUnit.States.cs)가 정하고, "어떻게 움직일지"는 보이드 힘의 합으로 정한다.
///
///  Patrol  : 거점 안에서 무리 지어 배회. 적 발견 → 진영 경보 → Engage
///  March   : 거점이 진군 명령을 내리면 무리 지어 목표로 행군. 적 발견 → Engage
///  Engage  : 표적을 골라 무리 지어 접근. 표적이 꽉 찼으면(대기열) 한 걸음 뒤에서 대기
///  Attack  : 표적 주위에 둥글게 붙어서 쿨다운마다 공격. 분리 힘 덕분에 자연스럽게 포위
///  Retreat : 체력이 낮거나 진영이 궤주하면 거점으로 도망쳐 회복
///  Dead    : 쓰러진 뒤 가라앉아 사라짐
/// </summary>
[RequireComponent(typeof(FactionBody))]
public partial class FactionUnit : MonoBehaviour
{
    [System.Serializable]
    public struct FlockWeights
    {
        public float separation;
        public float alignment;
        public float cohesion;
        public float goal;
        public float wander;

        public FlockWeights(float s, float a, float c, float g, float w)
        {
            separation = s; alignment = a; cohesion = c; goal = g; wander = w;
        }
    }

    [Header("References")]
    [Tooltip("소속 거점. FactionCamp가 생성하면 자동 지정")]
    public FactionCamp camp;

    [Header("Perception")]
    public float sightRange = 18f;
    [Tooltip("평소 시야각. 교전 중엔 사방을 봄")]
    [Range(0f, 360f)] public float sightAngle = 140f;
    [Tooltip("이 거리 안이면 시야와 상관없이 감지")]
    public float closeSenseRange = 4f;
    public float eyeHeight = 1.2f;
    [Tooltip("시야를 가리는 레이어 (벽, 바위 등)")]
    public LayerMask sightBlockMask;
    [Tooltip("표적 재선정 주기 (초)")]
    public float scanInterval = 0.4f;
    [Tooltip("표적에 이미 붙은 적 1마리당 이만큼 멀리 있는 것처럼 계산 → 표적이 분산됨")]
    public float crowdPenalty = 3f;

    [Header("Speed")]
    public float patrolSpeed = 1.5f;
    public float marchSpeed = 3.5f;
    public float engageSpeed = 5.5f;
    public float attackMoveSpeed = 2.5f;
    public float retreatSpeed = 6f;
    public float maxForce = 14f;
    public float turnSpeed = 8f;

    [Header("Flocking")]
    public float neighborRadius = 6f;
    public float separationRadius = 1.6f;
    //                                           분리  정렬  응집  목표  배회
    public FlockWeights patrolWeights = new FlockWeights(1.5f, 1.0f, 1.0f, 1.0f, 0.5f);
    public FlockWeights marchWeights = new FlockWeights(1.5f, 1.2f, 0.8f, 1.2f, 0.0f);
    public FlockWeights engageWeights = new FlockWeights(2.0f, 0.4f, 0.2f, 1.5f, 0.0f);
    public FlockWeights retreatWeights = new FlockWeights(1.5f, 0.5f, 0.3f, 1.5f, 0.0f);
    public float patrolDamping = 0.5f;

    [Header("Combat")]
    public float attackRange = 1.2f;
    public float attackDamage = 10f;
    public float attackCooldown = 1.5f;
    [Tooltip("쿨다운에 섞는 무작위 값. 모두가 같은 박자로 때리지 않게")]
    public float cooldownJitter = 0.4f;
    [Tooltip("표적 하나에 동시에 붙을 수 있는 최대 수. 나머지는 뒤에서 대기")]
    public int maxAttackersPerTarget = 4;
    [Tooltip("대기열이 서는 거리 (공격 사거리 바깥으로 얼마나)")]
    public float waitRingOffset = 2.5f;
    [Tooltip("포위 중 동료 간 거리")]
    public float attackSeparationRadius = 1.8f;
    public float attackSeparationWeight = 3f;
    public float attackDamping = 1f;
    public UnityEvent onAttack;

    [Header("Retreat")]
    [Tooltip("체력 비율이 이 아래면 거점으로 퇴각 (거점 안에서는 버팀)")]
    [Range(0f, 1f)] public float retreatHealth = 0.3f;
    [Tooltip("거점에서 이만큼 회복하면 다시 전투 복귀")]
    [Range(0f, 1f)] public float regroupHealth = 0.8f;
    public float homeHealPerSecond = 8f;

    [Header("Death")]
    public float corpseLifetime = 3f;

    [Header("Environment")]
    [Tooltip("지면 레이어만 (몬스터 레이어 제외)")]
    public LayerMask groundMask = 1;
    [Tooltip("피해야 할 장애물 레이어")]
    public LayerMask obstacleMask;
    public float avoidDistance = 3f;
    public float avoidWeight = 4f;
    public float bodyRadius = 0.5f;
    public float maxStepHeight = 0.6f;

    [Header("Visual")]
    [Tooltip("렌더러 색을 진영 색으로 칠함")]
    public bool tintByFaction = true;

    public FactionBody Body { get; private set; }
    public FactionBody Target { get; private set; }
    public Vector3 Velocity { get; private set; }
    public string StateName => state != null ? state.Name : "-";

    // 상태 인스턴스 (한 번 만들어 재사용)
    State state;
    PatrolState patrol;
    MarchState march;
    EngageState engage;
    AttackState attack;
    RetreatState retreat;
    DeadState dead;

    readonly List<FactionBody> neighbors = new List<FactionBody>();
    readonly List<FactionBody> scanBuffer = new List<FactionBody>();
    Vector3 spawnPosition;
    float scanTimer;
    float wanderAngle;
    float nextAttackTime;
    FactionBody nearestThreat;

    Vector3 Home => camp != null ? camp.Center : spawnPosition;
    float HomeRadius => camp != null ? camp.homeRadius : 8f;
    bool AtHome => camp != null ? camp.IsInHome(transform.position, 0.7f)
                                : Flat(spawnPosition - transform.position).magnitude < HomeRadius * 0.7f;
    bool HasTarget => Target != null && Target.IsAlive && InTerritory(Target.Position);
    bool ShouldRetreat => Body.HealthRatio < retreatHealth && !AtHome;
    /// <summary>전투가 끝나면 돌아갈 상태</summary>
    State CalmState => camp != null && camp.IsMarching ? (State)march : patrol;

    // ───────── 생명주기 ─────────

    void Awake()
    {
        Body = GetComponent<FactionBody>();
        patrol = new PatrolState(this);
        march = new MarchState(this);
        engage = new EngageState(this);
        attack = new AttackState(this);
        retreat = new RetreatState(this);
        dead = new DeadState(this);
    }

    void Start()
    {
        spawnPosition = transform.position;
        wanderAngle = Random.Range(0f, 360f);
        scanTimer = Random.Range(0f, scanInterval); // 탐색 프레임을 유닛마다 흩어 놓음

        if (camp != null) camp.AddMember(this);
        if (tintByFaction) ApplyFactionColor();

        ChangeState(patrol);

        if (FactionManager.Instance != null) FactionManager.Instance.RegisterUnit(this);
        else Debug.LogWarning("씬에 FactionManager가 없습니다.", this);
    }

    void OnDestroy()
    {
        state?.Exit(); // Attack 중이었다면 표적의 attackers 카운트 정리
        if (camp != null) camp.RemoveMember(this);
        if (FactionManager.Instance != null) FactionManager.Instance.UnregisterUnit(this);
    }

    /// <summary>FactionManager가 매 프레임 호출</summary>
    public void Tick(float dt)
    {
        // 어느 상태에서든 죽으면 Dead로 (Any State 전이)
        if (!Body.IsAlive && state != dead) ChangeState(dead);
        if (state == dead) { state.Tick(dt); return; }

        FactionManager.Instance.GetNearby(transform.position, neighborRadius, neighbors, Body);

        if ((state == patrol || state == retreat) && AtHome) Body.Heal(homeHealPerSecond * dt);

        Vector3 steer = state.Tick(dt);       // 이 안에서 상태가 바뀔 수 있음
        float speed = state.Speed;
        steer += AvoidObstacles(speed) * avoidWeight;

        steer = Vector3.ClampMagnitude(Flat(steer), maxForce);
        Velocity = Vector3.ClampMagnitude(Flat(Velocity + steer * dt), speed);

        Move(dt);
        Face(dt);
    }

    void ChangeState(State next)
    {
        if (next == state) return;
        state?.Exit();
        state = next;
        state.Enter();
    }

    // ───────── 외부 이벤트 ─────────

    /// <summary>진영 경보를 받았을 때</summary>
    public void OnAlerted(FactionBody enemy)
    {
        if (state != patrol && state != march) return;
        if (enemy == null || !enemy.IsAlive || !InTerritory(enemy.Position)) return;
        Target = enemy;
        ChangeState(engage);
    }

    /// <summary>FactionBody가 피격 시 호출. 쉬다가 맞으면 반격</summary>
    public void OnDamaged(FactionBody attacker)
    {
        if (!Body.IsAlive || attacker == null || !attacker.IsAlive) return;
        if (state != patrol && state != march) return;
        Target = attacker;
        RaiseAlarm();
        ChangeState(engage);
    }

    /// <summary>진영 궤주</summary>
    public void OnRout()
    {
        if (Body.IsAlive) ChangeState(retreat);
    }

    void RaiseAlarm()
    {
        if (camp != null && Target != null) camp.Alert(Target);
    }

    // ───────── 감지 ─────────

    /// <summary>
    /// scanInterval마다 주변 적을 훑어 가장 좋은 표적과 가장 가까운 위협을 고른다.
    /// 점수 = 거리 + (이미 붙은 적 수 × crowdPenalty) - (현재 표적이면 보너스)
    /// </summary>
    bool Scan(float dt, bool alert)
    {
        scanTimer -= dt;
        if (scanTimer > 0f) return false;
        scanTimer = scanInterval;

        FactionManager.Instance.GetNearby(transform.position, sightRange, scanBuffer, Body);
        var fm = FactionManager.Instance;
        Vector3 pos = transform.position;
        Vector3 fwd = Flat(transform.forward);

        FactionBody best = null;
        float bestScore = float.MaxValue;
        nearestThreat = null;
        float nearest = float.MaxValue;

        foreach (var b in scanBuffer)
        {
            if (!fm.IsHostile(Body.faction, b.faction)) continue;

            Vector3 to = Flat(b.Position - pos);
            float d = to.magnitude;
            if (d < nearest) { nearest = d; nearestThreat = b; }

            if (!InTerritory(b.Position)) continue;
            if (!alert && d > closeSenseRange && Vector3.Angle(fwd, to) > sightAngle * 0.5f) continue;

            float score = d + b.attackers * crowdPenalty - (b == Target ? 2f : 0f);
            if (score >= bestScore) continue;
            if (d > closeSenseRange && !HasLineOfSight(b)) continue;

            best = b;
            bestScore = score;
        }

        scanResult = best;
        return true;
    }

    FactionBody scanResult;

    /// <summary>탐색 주기가 돌아오면 표적을 갱신. 유효한 표적이 있으면 true</summary>
    bool ScanForEnemy(float dt, bool alert)
    {
        if (Scan(dt, alert)) Target = scanResult;
        return HasTarget;
    }

    bool HasLineOfSight(FactionBody b)
    {
        if (sightBlockMask == 0) return true;
        Vector3 eye = transform.position + Vector3.up * eyeHeight;
        Vector3 aim = b.Position + Vector3.up * eyeHeight;
        return !Physics.Linecast(eye, aim, sightBlockMask, QueryTriggerInteraction.Ignore);
    }

    bool InTerritory(Vector3 p)
    {
        if (camp == null) return Flat(p - spawnPosition).magnitude <= 40f;
        return camp.IsMarching || !camp.IsOutsideLeash(p);
    }

    float Reach(FactionBody t) => attackRange + Body.radius + t.radius;
    float DistanceTo(FactionBody t) => Flat(t.Position - transform.position).magnitude;

    // ───────── 조향 (보이드) ─────────

    /// <summary>
    /// 분리는 주변 모든 몸체(적 포함) 대상, 정렬/응집은 같은 거점 동료끼리만
    /// </summary>
    void Flock(float sepRadius, float speed, out Vector3 sep, out Vector3 ali, out Vector3 coh)
    {
        sep = ali = coh = Vector3.zero;
        Vector3 pos = transform.position;
        Vector3 away = Vector3.zero, avgVel = Vector3.zero, center = Vector3.zero;
        int flockCount = 0, sepCount = 0;

        foreach (var n in neighbors)
        {
            Vector3 offset = Flat(pos - n.Position);
            float d = offset.magnitude;
            if (d < 0.001f)
            {
                Vector2 r = Random.insideUnitCircle.normalized;
                offset = new Vector3(r.x, 0f, r.y);
                d = 0.01f;
            }
            if (d < sepRadius)
            {
                away += offset.normalized * (1f - d / sepRadius) / d;
                sepCount++;
            }

            var mate = n.Unit;
            if (mate != null && n.faction == Body.faction && mate.camp == camp)
            {
                avgVel += mate.Velocity;
                center += n.Position;
                flockCount++;
            }
        }

        if (sepCount > 0) sep = SteerTowards(away, speed);
        if (flockCount > 0)
        {
            ali = Flat(avgVel / flockCount) - Velocity;
            coh = SteerTowards(center / flockCount - pos, speed);
        }
    }

    Vector3 Flocked(FlockWeights w, float speed)
    {
        Flock(separationRadius, speed, out var sep, out var ali, out var coh);
        return sep * w.separation + ali * w.alignment + coh * w.cohesion;
    }

    /// <summary>표적과의 거리를 desired로 유지 (반경 방향 속도만 조절 → 옆으로 도는 건 자유)</summary>
    Vector3 KeepDistance(FactionBody t, float desired, float speed)
    {
        Vector3 from = Flat(transform.position - t.Position);
        float d = from.magnitude;
        if (d < 0.01f) { from = -Flat(transform.forward); d = 0.01f; }
        Vector3 radial = from / d;

        float desiredRadial = Mathf.Clamp((desired - d) * 2f, -speed, speed);
        return radial * (desiredRadial - Vector3.Dot(Velocity, radial));
    }

    Vector3 HomeTether(float speed)
    {
        Vector3 offset = Flat(Home - transform.position);
        float d = offset.magnitude;
        float inner = HomeRadius * 0.5f;
        if (d <= inner) return Vector3.zero;
        float strength = Mathf.Clamp((d - inner) / (HomeRadius - inner), 0f, 3f);
        return SteerTowards(offset, speed) * strength;
    }

    Vector3 Arrive(Vector3 point, float speed, float slowRadius)
    {
        Vector3 offset = Flat(point - transform.position);
        float d = offset.magnitude;
        if (d < 0.05f) return -Velocity;
        return offset / d * (speed * Mathf.Clamp01(d / slowRadius)) - Velocity;
    }

    Vector3 Wander(float speed, float dt)
    {
        wanderAngle += Random.Range(-1f, 1f) * 120f * dt;
        float rad = wanderAngle * Mathf.Deg2Rad;
        return SteerTowards(new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad)), speed * 0.5f);
    }

    Vector3 AvoidObstacles(float speed)
    {
        if (obstacleMask == 0) return Vector3.zero;

        Vector3 dir = Velocity.sqrMagnitude > 0.01f ? Velocity.normalized : Flat(transform.forward).normalized;
        Vector3 origin = transform.position + Vector3.up * (bodyRadius + 0.1f);

        if (Physics.SphereCast(origin, bodyRadius, dir, out RaycastHit hit, avoidDistance,
                               obstacleMask, QueryTriggerInteraction.Ignore))
        {
            Vector3 normal = Flat(hit.normal).normalized;
            Vector3 slide = Flat(Vector3.ProjectOnPlane(dir, normal)).normalized;
            float strength = 1f - hit.distance / avoidDistance;
            return SteerTowards(normal + slide, speed) * strength;
        }
        return Vector3.zero;
    }

    Vector3 SteerTowards(Vector3 dir, float speed)
    {
        dir = Flat(dir);
        if (dir.sqrMagnitude < 1e-6f) return Vector3.zero;
        return dir.normalized * speed - Velocity;
    }

    // ───────── 전투 ─────────

    void TryAttack(FactionBody t)
    {
        if (Time.time < nextAttackTime) return;
        if (DistanceTo(t) > Reach(t)) return;

        Vector3 to = Flat(t.Position - transform.position);
        if (Vector3.Angle(Flat(transform.forward), to) > 45f) return; // 아직 돌아서는 중

        nextAttackTime = Time.time + attackCooldown + Random.Range(-cooldownJitter, cooldownJitter);
        onAttack?.Invoke();
        t.TakeDamage(attackDamage, Body);
    }

    // ───────── 이동 / 회전 ─────────

    void Move(float dt)
    {
        Vector3 p = transform.position + Velocity * dt;
        if (TryGetGround(p, out float groundY)) p.y = groundY;
        transform.position = p;
    }

    static readonly RaycastHit[] groundHits = new RaycastHit[8];

    /// <summary>지면 높이 찾기. 유닛/플레이어 등 FactionBody 콜라이더는 무시해서 머리 위로 올라타지 않게 한다</summary>
    bool TryGetGround(Vector3 p, out float groundY)
    {
        groundY = p.y;
        Vector3 origin = new Vector3(p.x, transform.position.y + maxStepHeight, p.z);
        int count = Physics.RaycastNonAlloc(origin, Vector3.down, groundHits, maxStepHeight + 10f,
                                            groundMask, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        bool found = false;
        for (int i = 0; i < count; i++)
        {
            var h = groundHits[i];
            if (h.collider.GetComponentInParent<FactionBody>() != null) continue;
            if (h.collider.GetComponentInParent<BoidMonster>() != null) continue;
            if (h.collider.GetComponentInParent<SimpleChaser>() != null) continue;
            if (h.distance < best)
            {
                best = h.distance;
                groundY = h.point.y;
                found = true;
            }
        }
        return found;
    }

    void Face(float dt)
    {
        Vector3 look = state == attack && Target != null ? Target.Position - transform.position : Velocity;
        look.y = 0f;
        if (look.sqrMagnitude < 0.0001f) return;
        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(look), turnSpeed * dt);
    }

    void ApplyFactionColor()
    {
        if (FactionManager.Instance == null) return;
        Color c = FactionManager.Instance.ColorOf(Body.faction);
        var mpb = new MaterialPropertyBlock();
        foreach (var r in GetComponentsInChildren<Renderer>())
        {
            r.GetPropertyBlock(mpb);
            mpb.SetColor("_BaseColor", c); // URP
            mpb.SetColor("_Color", c);     // Built-in
            r.SetPropertyBlock(mpb);
        }
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    // ───────── 디버그 ─────────

    void OnDrawGizmos()
    {
        if (state == null) return;
        Gizmos.color = state.GizmoColor;
        Gizmos.DrawSphere(transform.position + Vector3.up * (eyeHeight + 0.8f), 0.2f);

        if (Target != null && (state == engage || state == attack))
        {
            Gizmos.color = state == attack ? Color.red : new Color(1f, 0.6f, 0f);
            Gizmos.DrawLine(transform.position + Vector3.up, Target.Position + Vector3.up);
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 1f, 0f, 0.3f);
        Gizmos.DrawWireSphere(transform.position, sightRange);
    }
}
