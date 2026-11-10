using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 비교용 단순 추격 몬스터.
/// 동료를 전혀 신경 쓰지 않고, 플레이어를 발견하면 일직선으로 달려가 때린다.
/// 수치는 BoidMonster 기본값과 맞춰 두었다 (속도 5.5, 감지 20, 사거리 1.8, 쿨다운 2.5).
/// </summary>
public class SimpleChaser : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("비워두면 'Player' 태그로 찾음")]
    public Transform target;
    public float detectRange = 20f;
    [Tooltip("플레이어가 생성 위치에서 이 거리 밖으로 나가면 추격을 포기하고 복귀")]
    public float leashRange = 45f;

    [Header("Movement")]
    public float moveSpeed = 5.5f;
    public float turnSpeed = 8f;

    [Header("Attack")]
    public float attackRange = 1.8f;
    public float attackCooldown = 2.5f;
    public UnityEvent onAttack;

    [Header("Environment")]
    [Tooltip("지면 레이어만 (몬스터 레이어 제외)")]
    public LayerMask groundMask = 1;

    public bool IsChasing { get; private set; }

    Vector3 home;
    float nextAttackTime;

    void Start()
    {
        home = transform.position;
        if (target == null)
        {
            var player = GameObject.FindWithTag("Player");
            if (player != null) target = player.transform;
        }
    }

    void Update()
    {
        if (target == null) return;

        float distToTarget = Flat(target.position - transform.position).magnitude;
        float targetFromHome = Flat(target.position - home).magnitude;

        // 상태: 추격 or 귀가, 두 가지뿐
        if (!IsChasing && distToTarget <= detectRange && targetFromHome <= leashRange) IsChasing = true;
        if (IsChasing && targetFromHome > leashRange) IsChasing = false;

        // 목적지를 향해 일직선 이동 (모두 같은 지점을 향하므로 결국 한 점에 겹침)
        Vector3 destination = IsChasing ? target.position : home;
        Vector3 toDest = Flat(destination - transform.position);
        float stopDistance = IsChasing ? attackRange * 0.9f : 0.3f;

        if (toDest.magnitude > stopDistance)
        {
            Vector3 p = transform.position + toDest.normalized * moveSpeed * Time.deltaTime;
            if (Physics.Raycast(p + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 10f,
                                groundMask, QueryTriggerInteraction.Ignore))
                p.y = hit.point.y;
            transform.position = p;
        }

        if (toDest.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.Slerp(transform.rotation,
                                                  Quaternion.LookRotation(toDest), turnSpeed * Time.deltaTime);

        // 사거리 안이면 쿨다운마다 공격 (박자 분산 없음)
        if (IsChasing && distToTarget <= attackRange && Time.time >= nextAttackTime)
        {
            nextAttackTime = Time.time + attackCooldown;
            onAttack?.Invoke();
        }
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    void OnDrawGizmos()
    {
        Gizmos.color = IsChasing ? Color.yellow : Color.green;
        Gizmos.DrawSphere(transform.position + Vector3.up * 2f, 0.2f);
    }
}