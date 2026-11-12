using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 진영 거점. 무리를 생성하고 진영 단위의 결정을 내린다.
///  - 경보: 한 마리가 적을 발견하면 쉬던 동료 전체가 교전
///  - 진군: marchTarget이 있으면 일정 시간 뒤 무리 전체가 그쪽으로 행군
///  - 사기: 살아남은 비율이 routThreshold 아래로 떨어지면 전원 퇴각(궤주)하고 진군을 멈춤
/// </summary>
public class FactionCamp : MonoBehaviour
{
    [Header("Spawn")]
    public FactionUnit prefab;
    public Faction faction = Faction.Red;
    public int count = 12;
    public float spawnRadius = 6f;

    [Header("Territory")]
    [Tooltip("평소 무리가 머물고, 퇴각해서 회복하는 범위")]
    public float homeRadius = 10f;
    [Tooltip("진군 중이 아닐 때, 적이 거점 중심에서 이 거리 밖이면 쫓지 않음")]
    public float leashRadius = 40f;

    [Header("March")]
    [Tooltip("진군할 목표 (보통 적 거점). 비워두면 거점만 지킴")]
    public Transform marchTarget;
    [Tooltip("시작 후 이 시간이 지나면 진군")]
    public float marchDelay = 5f;

    [Header("Morale")]
    [Tooltip("살아남은 비율이 이 아래로 떨어지면 전원 퇴각. 0이면 끝까지 싸움")]
    [Range(0f, 1f)] public float routThreshold = 0.3f;

    readonly List<FactionUnit> members = new List<FactionUnit>();
    public IReadOnlyList<FactionUnit> Members => members;
    public Vector3 Center => transform.position;
    public bool IsMarching { get; private set; }
    public bool IsRouted { get; private set; }
    public Vector3 MarchPoint => marchTarget != null ? marchTarget.position : Center;

    int peakMembers;
    float marchTimer;

    void Start()
    {
        marchTimer = marchDelay;
        if (prefab == null) return;

        for (int i = 0; i < count; i++)
        {
            Vector2 r = Random.insideUnitCircle * spawnRadius;
            Vector3 p = transform.position + new Vector3(r.x, 0f, r.y);
            var m = Instantiate(prefab, p, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            m.camp = this;
            m.Body.faction = faction;
        }
    }

    void Update()
    {
        if (!IsMarching && !IsRouted && marchTarget != null)
        {
            marchTimer -= Time.deltaTime;
            if (marchTimer <= 0f) IsMarching = true;
        }

        CheckMorale();
    }

    public void AddMember(FactionUnit m)
    {
        if (members.Contains(m)) return;
        members.Add(m);
        peakMembers = Mathf.Max(peakMembers, members.Count);
    }

    public void RemoveMember(FactionUnit m) { members.Remove(m); }

    /// <summary>외부에서 진군 명령 (null이면 진군 취소)</summary>
    public void OrderMarch(Transform target)
    {
        marchTarget = target;
        IsMarching = target != null && !IsRouted;
    }

    /// <summary>진영 경보: 쉬거나 행군 중이던 동료들이 그 적과 교전을 시작한다</summary>
    public void Alert(FactionBody enemy)
    {
        for (int i = 0; i < members.Count; i++)
            if (members[i] != null) members[i].OnAlerted(enemy);
    }

    void CheckMorale()
    {
        if (IsRouted || routThreshold <= 0f || peakMembers == 0) return;

        int alive = 0;
        foreach (var m in members)
            if (m != null && m.Body.IsAlive) alive++;

        if (alive / (float)peakMembers >= routThreshold) return;

        IsRouted = true;
        IsMarching = false;
        foreach (var m in members)
            if (m != null) m.OnRout();
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

    void OnDrawGizmos()
    {
        Color c = FactionManager.Instance != null ? FactionManager.Instance.ColorOf(faction) : Color.green;
        Gizmos.color = c;
        Gizmos.DrawWireSphere(transform.position, homeRadius);
        Gizmos.DrawCube(transform.position + Vector3.up * 3f, new Vector3(0.3f, 6f, 0.3f)); // 깃발

        if (marchTarget != null)
        {
            Gizmos.color = new Color(c.r, c.g, c.b, 0.5f);
            Gizmos.DrawLine(transform.position + Vector3.up, marchTarget.position + Vector3.up);
        }
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0f, 0f, 0.5f);
        Gizmos.DrawWireSphere(transform.position, leashRadius);
    }
}
