using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 진영 소속 몬스터. 스스로 판단하는 건 "내 눈에 뭐가 보이나"와 "지금 내 몸을 어떻게 움직이나"뿐이고,
/// 평온/경계/전투 같은 큰 판단은 소속 FactionCamp가 내려서 알려준다.
///
/// 개인 FSM (FactionUnit.States.cs)
///  Idle        : 역할 행동. 경비(Guard)는 자리 지키기, 배회조(Wanderer)는 무리 지어 순찰
///  Investigate : 진영이 지목한 수상한 지점으로 가서 둘러봄
///  Engage      : 진영이 아는 적에게 접근. 공격 슬롯이 없으면 주위를 돌며 순번 대기
///  Attack      : 슬롯을 얻은 동안 붙어서 공격. 몇 번 때리면 슬롯을 양보
///  Return      : 제자리로 돌아가며 체력 회복 (이 동안 적을 무시)
///  Dead        : 쓰러져 가라앉고 사라짐
///
/// 이동은 전부 보이드 힘(분리/정렬/응집 + 목표 힘)의 합이다.
/// </summary>
[RequireComponent(typeof(FactionBody))]
public partial class FactionUnit : MonoBehaviour
{
    public enum Role
    {
        [Tooltip("정해진 자리를 지킴")]
        Guard,
        [Tooltip("무리 지어 배회하거나 순찰 경로를 돎")]
        Wanderer
    }

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

    [Header("Role")]
    public Role role = Role.Wanderer;
    [Tooltip("소속 진영. FactionCamp가 생성하면 자동 지정. 진영 없이는 동작하지 않음")]
    public FactionCamp camp;

    [Header("Perception")]
    public float sightRange = 16f;
    [Tooltip("평온할 때 시야각. 경계 중엔 1.5배, 전투 중엔 사방을 봄")]
    [Range(0f, 360f)] public float sightAngle = 120f;
    [Tooltip("이 거리 안이면 시야와 상관없이 즉시 알아챔")]
    public float closeSenseRange = 3f;
    public float eyeHeight = 1.2f;
    [Tooltip("시야를 가리는 레이어 (벽, 바위 등)")]
    public LayerMask sightBlockMask;
    public float scanInterval = 0.3f;
    [Tooltip("시야에 적이 있을 때 초당 인지도 증가량 (가까울수록 최대 4배 빨라짐)")]
    public float awarenessGain = 0.6f;
    [Tooltip("시야에서 사라지면 초당 인지도 감소량")]
    public float awarenessDecay = 0.25f;
    [Tooltip("인지도가 이 이상이면 진영에 '수상하다' 보고 → 경계. 1이 되면 '적이다' 보고 → 전투")]
    [Range(0f, 1f)] public float suspicionLevel = 0.35f;

    [Header("Speed")]
    public float walkSpeed = 1.6f;
    public float investigateSpeed = 2.5f;
    public float chaseSpeed = 5.5f;
    public float attackMoveSpeed = 2.5f;
    public float returnSpeed = 4.5f;
    public float maxForce = 14f;
    public float turnSpeed = 8f;

    [Header("Flocking")]
    public float neighborRadius = 6f;
    public float separationRadius = 1.6f;
    //                                           분리  정렬  응집  목표  배회
    public FlockWeights idleWeights = new FlockWeights(1.5f, 1.0f, 1.0f, 1.0f, 0.5f);
    public FlockWeights patrolWeights = new FlockWeights(1.5f, 1.2f, 0.8f, 1.2f, 0.0f);
    public FlockWeights chaseWeights = new FlockWeights(2.0f, 0.4f, 0.2f, 1.5f, 0.0f);
    public FlockWeights returnWeights = new FlockWeights(1.5f, 0.5f, 0.3f, 1.5f, 0.0f);
    [Tooltip("평온할 때의 감속. 높을수록 차분히 머묾")]
    public float idleDamping = 0.5f;

    [Header("Combat")]
    public float attackRange = 1.2f;
    public float attackDamage = 10f;
    public float attackCooldown = 1.5f;
    [Tooltip("쿨다운에 섞는 무작위 값. 모두가 같은 박자로 때리지 않게")]
    public float cooldownJitter = 0.4f;
    [Tooltip("슬롯을 얻으면 이만큼 때리고 다음 몬스터에게 양보")]
    public int attacksPerTurn = 2;
    [Tooltip("슬롯을 양보한 뒤 다시 요청하기까지 쉬는 시간")]
    public float slotCooldown = 1.5f;
    [Tooltip("표적 하나에 붙은 적 1마리당 이만큼 멀리 있는 것처럼 계산 → 표적이 분산됨")]
    public float crowdPenalty = 3f;
    [Tooltip("순번을 기다리는 원이 공격 사거리 바깥으로 얼마나 떨어져 있을지")]
    public float waitRingOffset = 2.5f;
    [Tooltip("포위/대기 중 동료 간 거리")]
    public float combatSeparationRadius = 1.8f;
    public float combatSeparationWeight = 3f;
    public float combatDamping = 1f;
    [Tooltip("대기 중 좌우로 서성이는 정도")]
    public float swayWeight = 0.4f;
    public UnityEvent onAttack;

    [Header("Idle Look")]
    [Tooltip("평소에 두리번거리는 범위 (좌우 각도). 경비는 맡은 방향 기준, 배회조는 지금 방향 기준")]
    public float lookAroundAngle = 70f;
    [Tooltip("시선을 바꾸는 간격 (초, 최소~최대)")]
    public Vector2 lookAroundInterval = new Vector2(2f, 5f);
    [Tooltip("두리번거릴 때 회전 속도 (turnSpeed 대비 비율)")]
    [Range(0.05f, 1f)] public float lookTurnRatio = 0.3f;
    [Tooltip("이 속도 이상으로 움직이면 진행 방향을 봄")]
    public float faceMinSpeed = 0.05f;

    [Header("Investigate / Return")]
    [Tooltip("조사 지점에 도착해서 둘러보는 시간")]
    public float lookAroundTime = 3f;
    [Tooltip("복귀 중 초당 회복량 (RPG의 '리쉬 리셋')")]
    public float returnHealPerSecond = 25f;

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
    [Tooltip("한 번에 올라설 수 있는 최대 높이. 동료나 플레이어 머리 위로 올라타는 것을 막는다")]
    public float maxStepHeight = 0.6f;

    [Header("Visual")]
    [Tooltip("렌더러 색을 진영 색으로 칠함")]
    public bool tintByFaction = true;

    public FactionBody Body { get; private set; }
    public FactionBody Target { get; private set; }
    public Vector3 Velocity { get; private set; }
    /// <summary>0~1. 머리 위 ?/! 표시 같은 UI에 쓰면 됨</summary>
    public float Awareness { get; private set; }
    public string StateName => state != null ? state.Name : "-";
    public bool IsIdle => state == idle;
    public bool CanInvestigate => state == idle || state == investigate || state == engage || state == attack;

    // 상태 인스턴스 (한 번 만들어 재사용)
    State state;
    IdleState idle;
    InvestigateState investigate;
    EngageState engage;
    AttackState attack;
    ReturnState returning;
    DeadState dead;

    readonly List<FactionBody> neighbors = new List<FactionBody>();
    readonly List<FactionBody> scanBuffer = new List<FactionBody>();
    Vector3 postPosition;
    Quaternion postRotation = Quaternion.identity;
    bool hasPost;
    float scanTimer;
    float wanderAngle;
    float swayDir = 1f;
    float swayTimer;
    float idleLookYaw;
    float idleLookTimer;
    float nextAttackTime;
    float nextSlotTime;
    bool reportedSuspicion;
    FactionBody visibleEnemy;
    float visibleDistance;

    Vector3 Home => camp != null ? camp.Center : postPosition;
    float HomeRadius => camp != null ? camp.homeRadius : 8f;
    float DistanceToPost => Flat(postPosition - transform.position).magnitude;

    // ───────── 생명주기 ─────────

    void Awake()
    {
        Body = GetComponent<FactionBody>();
        idle = new IdleState(this);
        investigate = new InvestigateState(this);
        engage = new EngageState(this);
        attack = new AttackState(this);
        returning = new ReturnState(this);
        dead = new DeadState(this);
        ChangeState(idle);
    }

    void Start()
    {
        if (!hasPost) SetPost(transform.position, transform.rotation);
        wanderAngle = Random.Range(0f, 360f);
        swayDir = Random.value < 0.5f ? -1f : 1f;
        idleLookYaw = transform.eulerAngles.y;
        idleLookTimer = Random.Range(0f, lookAroundInterval.y);
        scanTimer = Random.Range(0f, scanInterval); // 탐색 프레임을 몬스터마다 흩어 놓음

        if (camp != null) camp.AddMember(this);
        else Debug.LogWarning("FactionUnit은 FactionCamp에 소속되어야 움직입니다.", this);

        if (tintByFaction) ApplyFactionColor();
    }

    void OnDestroy()
    {
        state?.Exit(); // Attack 중이었다면 표적의 슬롯 반납
        if (camp != null) camp.RemoveMember(this);
    }

    public void SetPost(Vector3 position, Quaternion rotation)
    {
        postPosition = position;
        postRotation = rotation;
        hasPost = true;
    }

    /// <summary>FactionCamp가 매 프레임 호출</summary>
    public void Tick(float dt)
    {
        // 어느 상태에서든 죽으면 Dead로 (Any State 전이)
        if (!Body.IsAlive && state != dead) ChangeState(dead);
        if (state == dead) { state.Tick(dt); return; }

        FactionManager.Instance.GetNearby(transform.position, neighborRadius, neighbors, Body);
        Perceive(dt);

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

    // ───────── 진영 → 몬스터 명령 ─────────

    public void OnCampCombat()
    {
        if (state == idle || state == investigate || state == returning) ChangeState(engage);
    }

    public void OnCampCalm()
    {
        if (state == engage || state == attack || state == investigate) ChangeState(returning);
    }

    public void BeginInvestigate()
    {
        if (state == investigate) investigate.Restart();
        else if (CanInvestigate) ChangeState(investigate);
    }

    /// <summary>조사조로 뽑히지 않은 몬스터: 싸우던 중이면 복귀</summary>
    public void StandDown()
    {
        if (state == engage || state == attack) ChangeState(returning);
    }

    /// <summary>진영이 잠들 때: 제자리, 체력 회복, 평온</summary>
    public void ResetToPost()
    {
        if (!Body.IsAlive) return;
        transform.SetPositionAndRotation(postPosition, postRotation);
        Velocity = Vector3.zero;
        Body.ResetHealth();
        Awareness = 0f;
        reportedSuspicion = false;
        Target = null;
        ChangeState(idle);
    }

    /// <summary>FactionBody가 피격 시 호출. 맞으면 인지 단계를 건너뛰고 바로 진영에 알림</summary>
    public void OnDamaged(FactionBody attacker)
    {
        if (!Body.IsAlive || camp == null || attacker == null) return;
        Awareness = 1f;
        camp.ReportSighting(attacker);
    }

    // ───────── 감지 (인지도) ─────────

    /// <summary>
    /// 적을 보면 바로 전투가 아니라 인지도가 차오른다.
    /// 수상함(suspicionLevel) → 진영 경계, 확신(1) → 진영 전투. 가까울수록 빨리 차오름.
    /// </summary>
    void Perceive(float dt)
    {
        scanTimer -= dt;
        if (scanTimer <= 0f)
        {
            scanTimer = scanInterval;
            ScanVisible();
        }

        if (camp == null || state == returning) return; // 복귀 중엔 무시

        if (visibleEnemy == null || !visibleEnemy.IsAlive)
        {
            Awareness = Mathf.Max(0f, Awareness - awarenessDecay * dt);
            if (Awareness < suspicionLevel * 0.5f) reportedSuspicion = false;
            return;
        }

        if (camp.State == CampState.Combat)
        {
            Awareness = 1f;
            camp.ReportSighting(visibleEnemy); // 전투 중엔 본 위치를 계속 공유
            return;
        }

        float closeness = 1f - Mathf.Clamp01(visibleDistance / sightRange);
        float gain = awarenessGain * (1f + closeness * 3f) * (camp.State == CampState.Alert ? 2f : 1f);
        Awareness = visibleDistance <= closeSenseRange ? 1f : Mathf.Min(1f, Awareness + gain * dt);

        if (Awareness >= 1f)
        {
            camp.ReportSighting(visibleEnemy);
        }
        else if (Awareness >= suspicionLevel && !reportedSuspicion)
        {
            reportedSuspicion = true;
            camp.ReportSuspicion(visibleEnemy.Position);
        }
    }

    void ScanVisible()
    {
        visibleEnemy = null;
        visibleDistance = float.MaxValue;

        FactionManager.Instance.GetNearby(transform.position, sightRange, scanBuffer, Body);
        var fm = FactionManager.Instance;
        Vector3 pos = transform.position;
        Vector3 fwd = Flat(transform.forward);

        float angle = sightAngle;
        if (camp != null && camp.State == CampState.Alert) angle = Mathf.Min(360f, sightAngle * 1.5f);
        if (camp != null && camp.State == CampState.Combat) angle = 360f;

        foreach (var b in scanBuffer)
        {
            if (!fm.IsHostile(Body.faction, b.faction)) continue;

            Vector3 to = Flat(b.Position - pos);
            float d = to.magnitude;
            if (d >= visibleDistance) continue;
            if (d > closeSenseRange)
            {
                if (Vector3.Angle(fwd, to) > angle * 0.5f) continue;
                if (!HasLineOfSight(b)) continue;
            }
            visibleEnemy = b;
            visibleDistance = d;
        }
    }

    bool HasLineOfSight(FactionBody b)
    {
        if (sightBlockMask == 0) return true;
        Vector3 eye = transform.position + Vector3.up * eyeHeight;
        Vector3 aim = b.Position + Vector3.up * eyeHeight;
        return !Physics.Linecast(eye, aim, sightBlockMask, QueryTriggerInteraction.Ignore);
    }

    float Reach(FactionBody t) => attackRange + Body.radius + t.radius;
    float DistanceTo(FactionBody t) => Flat(t.Position - transform.position).magnitude;

    // ───────── 조향 (보이드) ─────────

    /// <summary>
    /// 분리는 주변 모든 몸체(적 포함) 대상, 정렬/응집은 같은 진영 동료끼리만
    /// (옆 진영 무리와 섞이지 않게)
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
            if (mate != null && mate.camp == camp)
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

    /// <summary>표적 주위를 좌우로 천천히 서성이는 힘 (대기 중인 몬스터가 살아 보이게)</summary>
    Vector3 Sway(FactionBody t, float speed, float dt)
    {
        swayTimer -= dt;
        if (swayTimer <= 0f)
        {
            swayTimer = Random.Range(1.5f, 4f);
            swayDir = Random.value < 0.5f ? -1f : 1f;
        }

        Vector3 radial = Flat(transform.position - t.Position);
        if (radial.sqrMagnitude < 0.0001f) return Vector3.zero;
        Vector3 tangent = Vector3.Cross(Vector3.up, radial.normalized) * swayDir;
        return tangent * (speed * 0.5f - Vector3.Dot(Velocity, tangent));
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

    /// <summary>쿨다운, 사거리, 방향이 맞으면 한 대 때림. 때렸으면 true</summary>
    bool TryAttack(FactionBody t)
    {
        if (Time.time < nextAttackTime) return false;
        if (DistanceTo(t) > Reach(t)) return false;

        Vector3 to = Flat(t.Position - transform.position);
        if (Vector3.Angle(Flat(transform.forward), to) > 45f) return false; // 아직 돌아서는 중

        nextAttackTime = Time.time + attackCooldown + Random.Range(-cooldownJitter, cooldownJitter);
        onAttack?.Invoke();
        t.TakeDamage(attackDamage, Body);
        return true;
    }

    // ───────── 이동 / 회전 ─────────

    void Move(float dt)
    {
        Vector3 p = transform.position + Velocity * dt;
        if (TryGetGround(p, out float groundY)) p.y = groundY;
        transform.position = p;
    }

    static readonly RaycastHit[] groundHits = new RaycastHit[8];

    /// <summary>지면 높이 찾기. 몬스터/플레이어 콜라이더는 무시해서 머리 위로 올라타지 않게 한다</summary>
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
        Vector3 look;
        float turn = turnSpeed;
        if ((state == attack || state == engage) && Target != null)
            look = Target.Position - transform.position;
        else if (state == idle && camp != null && camp.State == CampState.Alert)
            look = camp.InvestigatePoint - transform.position;    // 경계 중엔 수상한 쪽을 쳐다봄
        else if (Velocity.sqrMagnitude > faceMinSpeed * faceMinSpeed)
            look = Velocity;
        else if (state == idle)
        {
            look = IdleLookDirection(dt);                          // 서 있을 땐 천천히 두리번거림
            turn *= lookTurnRatio;
        }
        else
            return;

        look.y = 0f;
        if (look.sqrMagnitude < 0.0001f) return;
        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(look), turn * dt);
    }

    /// <summary>
    /// 몇 초마다 시선을 새로 고른다. 경비는 맡은 방향 ±lookAroundAngle 안에서,
    /// 배회조는 지금 보는 방향 기준으로. 시야가 따라 돌기 때문에 등 뒤도 언젠가는 보게 된다.
    /// </summary>
    Vector3 IdleLookDirection(float dt)
    {
        idleLookTimer -= dt;
        if (idleLookTimer <= 0f)
        {
            idleLookTimer = Random.Range(lookAroundInterval.x, lookAroundInterval.y);
            float baseYaw = role == Role.Guard ? postRotation.eulerAngles.y : transform.eulerAngles.y;
            idleLookYaw = baseYaw + Random.Range(-lookAroundAngle, lookAroundAngle);
        }
        return Quaternion.Euler(0f, idleLookYaw, 0f) * Vector3.forward;
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
        Vector3 head = transform.position + Vector3.up * (eyeHeight + 0.8f);
        Gizmos.color = state.GizmoColor;
        Gizmos.DrawSphere(head, 0.2f);

        // 인지도 막대 (평온/경계 중에만)
        if (Awareness > 0f && state != engage && state != attack)
        {
            Gizmos.color = Awareness >= suspicionLevel ? Color.yellow : Color.white;
            Gizmos.DrawCube(head + Vector3.up * 0.4f, new Vector3(0.1f, Awareness, 0.1f));
        }

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
        if (hasPost)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(transform.position, postPosition);
        }
    }
}
