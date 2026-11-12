using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 진영 소속 + 체력.
/// 진영 몬스터뿐 아니라 플레이어에게도 붙여서(faction = Player) 공격 대상이 되게 한다.
/// </summary>
public class FactionBody : MonoBehaviour
{
    public Faction faction = Faction.Red;
    public float maxHealth = 100f;
    [Tooltip("몸통 반지름. 공격 사거리 계산에 더해짐")]
    public float radius = 0.5f;

    public UnityEvent<float> onDamaged;
    public UnityEvent onDeath;

    public float Health { get; private set; }
    public bool IsAlive => Health > 0f;
    public float HealthRatio => maxHealth > 0f ? Health / maxHealth : 0f;
    public Vector3 Position => transform.position;
    public FactionUnit Unit { get; private set; }
    public FactionBody LastAttacker { get; private set; }

    /// <summary>지금 이 대상에 붙어서 때리고 있는 적의 수. 표적 분산과 대기열에 쓴다</summary>
    [System.NonSerialized] public int attackers;

    bool started;

    void Awake()
    {
        Health = maxHealth;
        Unit = GetComponent<FactionUnit>();
    }

    void Start()
    {
        started = true;
        Register();
    }

    void OnEnable() { if (started) Register(); }

    void OnDisable()
    {
        if (FactionManager.Instance != null) FactionManager.Instance.UnregisterBody(this);
    }

    void Register()
    {
        if (FactionManager.Instance != null) FactionManager.Instance.RegisterBody(this);
        else Debug.LogWarning("씬에 FactionManager가 없습니다.", this);
    }

    public void TakeDamage(float amount, FactionBody attacker)
    {
        if (!IsAlive || amount <= 0f) return;

        Health = Mathf.Max(0f, Health - amount);
        LastAttacker = attacker;
        onDamaged?.Invoke(amount);
        if (Unit != null) Unit.OnDamaged(attacker);
        if (!IsAlive) onDeath?.Invoke();
    }

    public void Heal(float amount)
    {
        if (!IsAlive) return;
        Health = Mathf.Min(maxHealth, Health + amount);
    }
}
