using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 보이드 힘만으로 움직이는 몬스터.
///  - 평소: 거점 안에서 무리를 지어 어슬렁거림 (분리/정렬/응집 + 거점 끈)
///  - 발견: 거점 전체에 경보 → 무리 지어 추격
///  - 포위: "링 힘"으로 플레이어와 일정 거리를 유지하고, 강한 분리로 원 위에 저절로 고르게 퍼짐
///  - 공격: 토큰을 얻은 몬스터만 예고 → 돌진 → 타격 → 원래 거리로 후퇴 (치고 빠지기)
///  - 복귀: 놓치거나 영역을 벗어나면 거점으로 돌아감
/// </summary>
public class BoidMonster : MonoBehaviour
{
    public enum State { Idle, Chase, Encircle, Windup, Lunge, Recover, Return }

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
    [Tooltip("비워두면 'Player' 태그로 찾음")]
    public Transform target;
    [Tooltip("소속 거점. BoidOutpost가 생성하면 자동 지정")]
    public BoidOutpost outpost;

    [Header("Perception")]
    public float sightRange = 20f;
    [Tooltip("평소 시야각. 전투 중엔 사방을 경계")]
    [Range(0f, 360f)] public float sightAngle = 140f;
    [Tooltip("이 거리 안이면 시야와 상관없이 감지 (소리, 기척)")]
    public float closeSenseRange = 4f;
    public float eyeHeight = 1.2f;
    [Tooltip("시야를 가리는 레이어 (벽, 바위 등)")]
    public LayerMask sightBlockMask;
    [Tooltip("플레이어를 놓친 뒤 이 시간이 지나면 포기하고 복귀")]
    public float forgetTime = 4f;

    [Header("Speed")]
    public float idleSpeed = 1.5f;
    public float chaseSpeed = 5.5f;
    public float encircleSpeed = 3f;
    public float lungeSpeed = 8f;
    public float maxForce = 14f;
    public float turnSpeed = 8f;

    [Header("Flocking")]
    public float neighborRadius = 6f;
    [Tooltip("평소/추격 중 동료와의 최소 거리")]
    public float separationRadius = 1.8f;
    //                                         분리  정렬  응집  목표  배회
    public FlockWeights idleWeights = new FlockWeights(1.5f, 1.0f, 1.0f, 1.0f, 0.5f);
    public FlockWeights chaseWeights = new FlockWeights(2.0f, 0.5f, 0.3f, 1.5f, 0.0f);
    public FlockWeights returnWeights = new FlockWeights(1.5f, 0.8f, 0.6f, 1.2f, 0.0f);

    [Header("Encircle (포위 대형)")]
    [Tooltip("포위할 때 플레이어와 유지하는 거리")]
    public float preferredDistance = 5f;
    [Tooltip("이 오차 안에서는 링 힘이 약해져 부드럽게 자리 잡음")]
    public float distanceTolerance = 1f;
    [Tooltip("공격할 때 말고는 절대 이보다 가까이 가지 않음")]
    public float personalSpace = 3f;
    [Tooltip("포위 중 동료 간 거리. 클수록 원 위에 넓게 퍼짐")]
    public float encircleSeparationRadius = 3.5f;
    public float encircleSeparationWeight = 3f;
    public float ringWeight = 2f;
    [Tooltip("원을 따라 좌우로 살짝 움직이는 정도")]
    public float strafeWeight = 0.3f;
    [Tooltip("포위 중 흔들림 억제")]
    public float damping = 1f;

    [Header("Attack (치고 빠지기)")]
    public float attackRange = 1.8f;
    public float attackCooldown = 2.5f;
    [Tooltip("쿨다운에 더해지는 랜덤 시간")]
    public float cooldownJitter = 1.5f;
    [Tooltip("돌진 전 예고 동작 시간")]
    public float windupTime = 0.5f;
    [Tooltip("돌진이 이 시간 안에 닿지 않으면 포기")]
    public float lungeTimeout = 1.2f;
    [Tooltip("타격 후 뒤로 물러나는 시간")]
    public float recoverTime = 0.8f;
    public UnityEvent onWindup;
    public UnityEvent onAttack;

    [Header("Environment")]
    [Tooltip("지면 레이어만 (몬스터 레이어 제외)")]
    public LayerMask groundMask = 1;
    [Tooltip("피해야 할 장애물 레이어")]
    public LayerMask obstacleMask;
    public float avoidDistance = 3f;
    public float avoidWeight = 4f;
    public float bodyRadius = 0.5f;

    public State CurrentState { get; private set; } = State.Idle;
    public Vector3 Velocity { get; private set; }

    readonly List<BoidMonster> neighbors = new List<BoidMonster>();
    Vector3 spawnPosition;
    Vector3 lastSeenPos;
    float lastSeenTime = -999f;
    bool canSee;
    bool hasToken;
    float stateTimer;
    float nextAttackTime;
    float wanderAngle;
    float strafeDir = 1f;
    float strafeTimer;

    Vector3 Home => outpost != null ? outpost.Center : spawnPosition;
    float HomeRadius => outpost != null ? outpost.homeRadius : 8f;
    Vector3 TargetPos => (canSee && target != null) ? target.position : lastSeenPos;

    bool OutsideLeash(Vector3 p) =>
        outpost != null ? outpost.IsOutsideLeash(p) : Flat(p - spawnPosition).magnitude > 40f;

    // ───────── 생명주기 ─────────

    void Start()
    {
        spawnPosition = transform.position;
        wanderAngle = Random.Range(0f, 360f);
        strafeDir = Random.value < 0.5f ? -1f : 1f;
        nextAttackTime = Time.time + Random.Range(0f, attackCooldown);

        if (target == null)
        {
            var player = GameObject.FindWithTag("Player");
            if (player != null) target = player.transform;
        }

        if (outpost != null) outpost.AddMember(this);

        if (BoidManager.Instance != null) BoidManager.Instance.Register(this);
        else Debug.LogWarning("씬에 BoidManager가 없습니다.", this);
    }

    void OnDestroy()
    {
        if (outpost != null) outpost.RemoveMember(this);
        if (BoidManager.Instance == null) return;
        if (hasToken) BoidManager.Instance.ReleaseAttackToken(target, this);
        BoidManager.Instance.Unregister(this);
    }

    /// <summary>BoidManager가 매 프레임 호출</summary>
    public void Tick(float dt)
    {
        stateTimer += dt;
        Perceive();
        UpdateState();

        BoidManager.Instance.GetNeighbors(transform.position, neighborRadius, neighbors, this);

        float speed = SpeedLimit();
        Vector3 steer = ComputeSteering(speed, dt);
        if (CurrentState != State.Windup) steer += AvoidObstacles(speed) * avoidWeight;

        steer = Vector3.ClampMagnitude(Flat(steer), maxForce);
        Velocity = Vector3.ClampMagnitude(Flat(Velocity + steer * dt), speed);

        Move(dt);
        Face(dt);
    }

    // ───────── 감지 ─────────

    void Perceive()
    {
        canSee = false;
        if (target == null) return;

        Vector3 toTarget = Flat(target.position - transform.position);
        float d = toTarget.magnitude;

        if (d <= closeSenseRange)
        {
            canSee = true;
        }
        else if (d <= sightRange)
        {
            bool alert = CurrentState != State.Idle && CurrentState != State.Return;
            bool inCone = alert || Vector3.Angle(Flat(transform.forward), toTarget) <= sightAngle * 0.5f;
            if (inCone)
            {
                Vector3 eye = transform.position + Vector3.up * eyeHeight;
                Vector3 aim = target.position + Vector3.up * eyeHeight;
                canSee = !Physics.Linecast(eye, aim, sightBlockMask, QueryTriggerInteraction.Ignore);
            }
        }

        if (canSee)
        {
            lastSeenPos = target.position;
            lastSeenTime = Time.time;
        }
    }

    // ───────── 상태 ─────────

    void UpdateState()
    {
        switch (CurrentState)
        {
            case State.Idle:
                if (canSee && !OutsideLeash(target.position)) BeginCombat();
                return;

            case State.Return:
                // 복귀 중엔 플레이어를 무시하고 거점까지 돌아감 (경계선 핑퐁 방지)
                if (Flat(Home - transform.position).magnitude < HomeRadius * 0.6f) SetState(State.Idle);
                return;
        }

        // 전투 중 공통: 놓쳤거나 영역을 벗어나면 복귀
        if (target == null || Time.time - lastSeenTime > forgetTime || OutsideLeash(target.position))
        {
            SetState(State.Return);
            return;
        }

        float d = Flat(TargetPos - transform.position).magnitude;

        switch (CurrentState)
        {
            case State.Chase:
                if (d <= preferredDistance + 2f) SetState(State.Encircle);
                break;

            case State.Encircle:
                if (d > preferredDistance + 6f)
                {
                    SetState(State.Chase);
                }
                else if (canSee && Time.time >= nextAttackTime &&
                         Mathf.Abs(d - preferredDistance) <= distanceTolerance * 1.5f &&
                         BoidManager.Instance.TryAcquireAttackToken(target, this))
                {
                    hasToken = true;
                    SetState(State.Windup);
                }
                break;

            case State.Windup:
                if (stateTimer >= windupTime) SetState(State.Lunge);
                break;

            case State.Lunge:
                if (d <= attackRange)
                {
                    onAttack?.Invoke();
                    SetState(State.Recover);
                }
                else if (stateTimer >= lungeTimeout)
                {
                    SetState(State.Recover);
                }
                break;

            case State.Recover:
                if (stateTimer >= recoverTime) SetState(State.Encircle);
                break;
        }
    }

    void SetState(State next)
    {
        if (next == CurrentState) return;

        // 토큰은 예고~돌진 동안만 쥐고, 후퇴를 시작하면 바로 다음 몬스터에게 넘긴다
        bool attacking = next == State.Windup || next == State.Lunge;
        if (!attacking && hasToken)
        {
            if (BoidManager.Instance != null) BoidManager.Instance.ReleaseAttackToken(target, this);
            hasToken = false;
        }

        if (next == State.Recover)
            nextAttackTime = Time.time + attackCooldown + Random.Range(0f, cooldownJitter);
        if (next == State.Windup)
            onWindup?.Invoke();

        CurrentState = next;
        stateTimer = 0f;
    }

    void BeginCombat()
    {
        SetState(State.Chase);
        if (outpost != null) outpost.Alert(target, lastSeenPos);
    }

    /// <summary>거점 경보를 받았을 때</summary>
    public void OnAlerted(Transform t, Vector3 seenPos)
    {
        if (CurrentState != State.Idle) return;
        target = t;
        lastSeenPos = seenPos;
        lastSeenTime = Time.time;
        SetState(State.Chase);
    }

    float SpeedLimit()
    {
        switch (CurrentState)
        {
            case State.Idle: return idleSpeed;
            case State.Chase: return chaseSpeed;
            case State.Return: return chaseSpeed * 0.8f;
            case State.Lunge: return lungeSpeed;
            case State.Recover: return encircleSpeed * 1.5f;
            default: return encircleSpeed;
        }
    }

    // ───────── 조향 ─────────

    Vector3 ComputeSteering(float speed, float dt)
    {
        Vector3 pos = transform.position;
        Vector3 sep, ali, coh;
        FlockWeights w;

        switch (CurrentState)
        {
            case State.Idle:
                w = idleWeights;
                Flock(separationRadius, speed, out sep, out ali, out coh);
                return sep * w.separation + ali * w.alignment + coh * w.cohesion
                     + HomeTether(speed) * w.goal + Wander(speed, dt) * w.wander;

            case State.Return:
                w = returnWeights;
                Flock(separationRadius, speed, out sep, out ali, out coh);
                return sep * w.separation + ali * w.alignment + coh * w.cohesion
                     + Arrive(Home, speed, 3f) * w.goal;

            case State.Chase:
                w = chaseWeights;
                Flock(separationRadius, speed, out sep, out ali, out coh);
                return sep * w.separation + ali * w.alignment + coh * w.cohesion
                     + SteerTowards(TargetPos - pos, speed) * w.goal;

            case State.Encircle:
                // 정렬/응집은 끄고, 넓은 분리 + 링 힘만으로 원형 대형이 저절로 생김
                Flock(encircleSeparationRadius, speed, out sep, out _, out _);
                return sep * encircleSeparationWeight
                     + RingForce(speed) * ringWeight
                     + Strafe(speed, dt) * strafeWeight
                     - Velocity * damping;

            case State.Windup:
                // 제자리에 멈춰 플레이어를 노려보며 예고 동작
                Flock(separationRadius, speed, out sep, out _, out _);
                return sep * 1.5f - Velocity * 4f;

            case State.Lunge:
                // 동료 회피 범위를 좁혀야 플레이어까지 파고들 수 있음
                Flock(bodyRadius * 2f, speed, out sep, out _, out _);
                return sep + SteerTowards(TargetPos - pos, speed) * 3f;

            case State.Recover:
                Flock(separationRadius, speed, out sep, out _, out _);
                return sep * 2f + SteerTowards(pos - TargetPos, speed) * 2f;
        }
        return Vector3.zero;
    }

    /// <summary>
    /// 분리는 모든 몬스터 대상, 정렬/응집은 같은 거점 소속끼리만
    /// (가까운 다른 거점 무리와 섞이지 않게)
    /// </summary>
    void Flock(float sepRadius, float speed, out Vector3 sep, out Vector3 ali, out Vector3 coh)
    {
        sep = ali = coh = Vector3.zero;
        Vector3 pos = transform.position;
        Vector3 away = Vector3.zero, avgVel = Vector3.zero, center = Vector3.zero;
        int flockCount = 0, sepCount = 0;

        foreach (var n in neighbors)
        {
            Vector3 offset = Flat(pos - n.transform.position);
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
            if (n.outpost == outpost)
            {
                avgVel += n.Velocity;
                center += n.transform.position;
                flockCount++;
            }
        }

        if (sepCount > 0) sep = SteerTowards(away, speed);
        if (flockCount > 0)
        {
            ali = SteerTowards(avgVel / flockCount, speed);
            coh = SteerTowards(center / flockCount - pos, speed);
        }
    }

    /// <summary>
    /// 링 힘: 플레이어와의 거리를 preferredDistance로 맞추는 반경 방향 힘.
    /// 반경 성분만 조절하므로 원을 따라 도는 움직임은 방해하지 않는다.
    /// </summary>
    Vector3 RingForce(float speed)
    {
        Vector3 fromTarget = Flat(transform.position - TargetPos);
        float d = fromTarget.magnitude;
        if (d < 0.01f) { fromTarget = -Flat(transform.forward); d = 0.01f; }
        Vector3 radial = fromTarget.normalized;

        float error = d - preferredDistance;
        float desiredRadial = -Mathf.Clamp(error / distanceTolerance, -1f, 1f) * speed;

        // 개인 공간 침범 시 강하게 밀어냄
        if (d < personalSpace)
            desiredRadial = speed * (1f + (personalSpace - d) / personalSpace);

        float currentRadial = Vector3.Dot(Velocity, radial);
        return radial * (desiredRadial - currentRadial);
    }

    /// <summary>원을 따라 좌우로 천천히 움직이며 간을 보는 힘</summary>
    Vector3 Strafe(float speed, float dt)
    {
        strafeTimer -= dt;
        if (strafeTimer <= 0f)
        {
            strafeTimer = Random.Range(1.5f, 4f);
            strafeDir = Random.value < 0.5f ? -1f : 1f;
        }

        Vector3 radial = Flat(transform.position - TargetPos);
        if (radial.sqrMagnitude < 0.0001f) return Vector3.zero;
        Vector3 tangent = Vector3.Cross(Vector3.up, radial.normalized) * strafeDir;
        return tangent * (speed * 0.5f - Vector3.Dot(Velocity, tangent));
    }

    /// <summary>거점 중심에서 멀어질수록 강해지는 끈</summary>
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

    // ───────── 이동 / 회전 ─────────

    void Move(float dt)
    {
        Vector3 p = transform.position + Velocity * dt;
        if (Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 10f,
                            groundMask, QueryTriggerInteraction.Ignore))
            p.y = hit.point.y;
        transform.position = p;
    }

    void Face(float dt)
    {
        bool faceTarget = target != null &&
            (CurrentState == State.Encircle || CurrentState == State.Windup ||
             CurrentState == State.Lunge || CurrentState == State.Recover);

        Vector3 look = faceTarget ? TargetPos - transform.position : Velocity;
        look.y = 0f;
        if (look.sqrMagnitude < 0.0001f) return;
        transform.rotation = Quaternion.Slerp(transform.rotation,
                                              Quaternion.LookRotation(look), turnSpeed * dt);
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    // ───────── 디버그 ─────────

    void OnDrawGizmos()
    {
        // 머리 위 색깔로 상태 표시
        switch (CurrentState)
        {
            case State.Idle: Gizmos.color = Color.green; break;
            case State.Chase: Gizmos.color = Color.yellow; break;
            case State.Encircle: Gizmos.color = new Color(1f, 0.5f, 0f); break;
            case State.Windup: Gizmos.color = Color.magenta; break;
            case State.Lunge: Gizmos.color = Color.red; break;
            case State.Recover: Gizmos.color = Color.blue; break;
            default: Gizmos.color = Color.gray; break;
        }
        Gizmos.DrawSphere(transform.position + Vector3.up * (eyeHeight + 0.8f), 0.2f);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 1f, 0f, 0.3f);
        Gizmos.DrawWireSphere(transform.position, sightRange);
        Gizmos.color = new Color(0f, 1f, 1f, 0.3f);
        Gizmos.DrawWireSphere(transform.position, encircleSeparationRadius);
        if (CurrentState != State.Idle && CurrentState != State.Return)
        {
            Gizmos.color = Color.white;
            Gizmos.DrawLine(transform.position, lastSeenPos);
        }
    }
}