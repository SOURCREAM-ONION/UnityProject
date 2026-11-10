using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 몬스터 거점. 무리를 생성하고, 평소 머무는 영역과 추격 한계(리쉬)를 정하며,
/// 한 마리가 플레이어를 발견하면 거점 전체에 경보를 울린다.
/// </summary>
public class BoidOutpost : MonoBehaviour
{
    [Header("Spawn")]
    public BoidMonster prefab;
    [Tooltip("비워두면 몬스터가 'Player' 태그로 직접 찾음")]
    public Transform target;
    public int count = 12;
    public float spawnRadius = 6f;

    [Header("Territory")]
    [Tooltip("평소 무리가 머무는 범위")]
    public float homeRadius = 10f;
    [Tooltip("플레이어가 거점 중심에서 이 거리 밖으로 나가면 추격을 포기하고 복귀")]
    public float leashRadius = 45f;

    readonly List<BoidMonster> members = new List<BoidMonster>();
    public IReadOnlyList<BoidMonster> Members => members;
    public Vector3 Center => transform.position;

    void Start()
    {
        if (prefab == null) return;
        for (int i = 0; i < count; i++)
        {
            Vector2 r = Random.insideUnitCircle * spawnRadius;
            Vector3 p = transform.position + new Vector3(r.x, 0f, r.y);
            var m = Instantiate(prefab, p, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            m.outpost = this;
            if (target != null) m.target = target;
        }
    }

    public void AddMember(BoidMonster m) { if (!members.Contains(m)) members.Add(m); }
    public void RemoveMember(BoidMonster m) { members.Remove(m); }

    /// <summary>거점 전체 경보: 쉬고 있던 동료들이 모두 추격을 시작한다</summary>
    public void Alert(Transform target, Vector3 seenPosition)
    {
        for (int i = 0; i < members.Count; i++)
            if (members[i] != null) members[i].OnAlerted(target, seenPosition);
    }

    public bool IsOutsideLeash(Vector3 p)
    {
        Vector3 d = p - Center;
        d.y = 0f;
        return d.sqrMagnitude > leashRadius * leashRadius;
    }

    void OnDrawGizmos()
    {
        Gizmos.color = new Color(0f, 1f, 0f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, homeRadius);
        Gizmos.color = new Color(1f, 0f, 1f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, spawnRadius);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0f, 0f, 0.5f);
        Gizmos.DrawWireSphere(transform.position, leashRadius);
    }
}