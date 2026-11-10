using UnityEngine;

/// <summary>비교용: SimpleChaser를 한 지점 주변에 생성한다. BoidOutpost와 같은 방식으로 배치하면 된다.</summary>
public class SimpleChaserSpawner : MonoBehaviour
{
    public SimpleChaser prefab;
    [Tooltip("비워두면 몬스터가 'Player' 태그로 직접 찾음")]
    public Transform target;
    public int count = 12;
    public float spawnRadius = 6f;

    void Start()
    {
        if (prefab == null) return;
        for (int i = 0; i < count; i++)
        {
            Vector2 r = Random.insideUnitCircle * spawnRadius;
            Vector3 p = transform.position + new Vector3(r.x, 0f, r.y);
            var m = Instantiate(prefab, p, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            if (target != null) m.target = target;
        }
    }

    void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0f, 1f, 0.6f);
        Gizmos.DrawWireSphere(transform.position, spawnRadius);
    }
}