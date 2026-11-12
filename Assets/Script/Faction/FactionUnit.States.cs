using UnityEngine;

/// <summary>
/// FactionUnit의 FSM 상태들.
/// 각 상태는 Tick에서 ① 전이 조건을 검사하고 ② 이번 프레임의 보이드 조향 힘을 돌려준다.
/// 중첩 클래스라서 FactionUnit의 private 멤버를 그대로 쓸 수 있다.
/// </summary>
public partial class FactionUnit
{
    public abstract class State
    {
        protected readonly FactionUnit u;
        protected State(FactionUnit unit) { u = unit; }

        public abstract string Name { get; }
        public abstract Color GizmoColor { get; }
        public abstract float Speed { get; }

        public virtual void Enter() { }
        public virtual void Exit() { }
        /// <summary>전이 검사 후 조향 힘을 반환</summary>
        public abstract Vector3 Tick(float dt);
    }

    // ───────── 평소: 거점 안에서 배회 ─────────
    class PatrolState : State
    {
        public PatrolState(FactionUnit u) : base(u) { }
        public override string Name => "Patrol";
        public override Color GizmoColor => Color.green;
        public override float Speed => u.patrolSpeed;

        public override Vector3 Tick(float dt)
        {
            if (u.camp != null && u.camp.IsMarching) { u.ChangeState(u.march); return Vector3.zero; }
            if (u.ScanForEnemy(dt, false))
            {
                u.RaiseAlarm();
                u.ChangeState(u.engage);
                return Vector3.zero;
            }

            float s = Speed;
            var w = u.patrolWeights;
            return u.Flocked(w, s)
                 + u.HomeTether(s) * w.goal
                 + u.Wander(s, dt) * w.wander
                 - u.Velocity * u.patrolDamping;
        }
    }

    // ───────── 진군: 무리 지어 목표로 행군 ─────────
    class MarchState : State
    {
        public MarchState(FactionUnit u) : base(u) { }
        public override string Name => "March";
        public override Color GizmoColor => Color.cyan;
        public override float Speed => u.marchSpeed;

        public override Vector3 Tick(float dt)
        {
            if (u.camp == null || !u.camp.IsMarching) { u.ChangeState(u.patrol); return Vector3.zero; }
            if (u.ScanForEnemy(dt, false))
            {
                u.RaiseAlarm();
                u.ChangeState(u.engage);
                return Vector3.zero;
            }

            float s = Speed;
            var w = u.marchWeights;
            return u.Flocked(w, s) + u.Arrive(u.camp.MarchPoint, s, 5f) * w.goal;
        }
    }

    // ───────── 교전: 표적으로 접근, 꽉 찼으면 대기열 ─────────
    class EngageState : State
    {
        public EngageState(FactionUnit u) : base(u) { }
        public override string Name => "Engage";
        public override Color GizmoColor => Color.yellow;
        public override float Speed => u.engageSpeed;

        public override void Enter() => u.scanTimer = 0f; // 들어오자마자 표적 다시 고름

        public override Vector3 Tick(float dt)
        {
            if (u.ShouldRetreat) { u.ChangeState(u.retreat); return Vector3.zero; }
            if (!u.ScanForEnemy(dt, true)) { u.ChangeState(u.CalmState); return Vector3.zero; }

            var t = u.Target;
            float s = Speed;
            float reach = u.Reach(t);
            float d = u.DistanceTo(t);
            bool full = t.attackers >= u.maxAttackersPerTarget;

            if (!full && d <= reach) { u.ChangeState(u.attack); return Vector3.zero; }

            // 표적이 꽉 찼고 가까우면, 한 걸음 뒤 대기열에 서서 빈자리를 기다림
            if (full && d < reach + u.waitRingOffset + 3f)
            {
                u.Flock(u.attackSeparationRadius, s, out var sep, out _, out _);
                return sep * u.attackSeparationWeight
                     + u.KeepDistance(t, reach + u.waitRingOffset, s) * 2f
                     - u.Velocity * u.attackDamping;
            }

            var w = u.engageWeights;
            return u.Flocked(w, s) + u.SteerTowards(t.Position - u.transform.position, s) * w.goal;
        }
    }

    // ───────── 공격: 표적 주위를 둥글게 감싸고 때림 ─────────
    class AttackState : State
    {
        FactionBody locked; // 들어올 때 잡은 표적. attackers 카운트를 정확히 되돌리기 위해 따로 보관

        public AttackState(FactionUnit u) : base(u) { }
        public override string Name => "Attack";
        public override Color GizmoColor => Color.red;
        public override float Speed => u.attackMoveSpeed;

        public override void Enter()
        {
            locked = u.Target;
            if (locked != null) locked.attackers++;
            // 첫 타 박자를 흩어 놓음
            u.nextAttackTime = Time.time + Random.Range(0.1f, u.attackCooldown * 0.5f);
        }

        public override void Exit()
        {
            if (locked != null) locked.attackers--;
            locked = null;
        }

        public override Vector3 Tick(float dt)
        {
            if (u.ShouldRetreat) { u.ChangeState(u.retreat); return Vector3.zero; }
            if (locked == null || !locked.IsAlive || !u.InTerritory(locked.Position))
            {
                u.ChangeState(u.engage);
                return Vector3.zero;
            }

            float s = Speed;
            float reach = u.Reach(locked);
            if (u.DistanceTo(locked) > reach * 1.4f) { u.ChangeState(u.engage); return Vector3.zero; }

            u.TryAttack(locked);

            // 응집은 끄고 넓은 분리 + 거리 유지 → 표적 주위에 원형 포위가 저절로 생김
            u.Flock(u.attackSeparationRadius, s, out var sep, out var ali, out _);
            return sep * u.attackSeparationWeight
                 + ali * 0.3f
                 + u.KeepDistance(locked, reach * 0.8f, s) * 2f
                 - u.Velocity * u.attackDamping;
        }
    }

    // ───────── 퇴각: 적을 피해 거점으로, 회복 후 복귀 ─────────
    class RetreatState : State
    {
        public RetreatState(FactionUnit u) : base(u) { }
        public override string Name => "Retreat";
        public override Color GizmoColor => new Color(0.6f, 0.3f, 1f);
        public override float Speed => u.retreatSpeed;

        public override void Enter()
        {
            u.Target = null;
            u.scanResult = null;
            u.scanTimer = 0f;
        }

        public override Vector3 Tick(float dt)
        {
            u.Scan(dt, true); // 표적은 안 잡고 가장 가까운 위협만 갱신

            if (u.AtHome)
            {
                // 거점 안까지 쫓아온 적은 맞서 싸움 (궁지에 몰림)
                var intruder = u.scanResult;
                if (intruder != null && intruder.IsAlive
                    && Flat(intruder.Position - u.Home).magnitude <= u.HomeRadius)
                {
                    u.Target = intruder;
                    u.ChangeState(u.engage);
                    return Vector3.zero;
                }
                if (u.Body.HealthRatio >= u.regroupHealth) { u.ChangeState(u.patrol); return Vector3.zero; }
            }

            float s = Speed;
            var w = u.retreatWeights;
            Vector3 steer = u.Flocked(w, s) + u.Arrive(u.Home, s, 3f) * w.goal;

            var threat = u.nearestThreat;
            if (threat != null && threat.IsAlive && u.DistanceTo(threat) < u.sightRange * 0.5f)
                steer += u.SteerTowards(u.transform.position - threat.Position, s);

            return steer;
        }
    }

    // ───────── 사망: 쓰러져 가라앉고 사라짐 ─────────
    class DeadState : State
    {
        float timer;

        public DeadState(FactionUnit u) : base(u) { }
        public override string Name => "Dead";
        public override Color GizmoColor => Color.black;
        public override float Speed => 0f;

        public override void Enter()
        {
            timer = 0f;
            u.Velocity = Vector3.zero;
            u.Target = null;
            foreach (var c in u.GetComponentsInChildren<Collider>()) c.enabled = false;
        }

        public override Vector3 Tick(float dt)
        {
            timer += dt;
            if (timer > 1f) u.transform.position += Vector3.down * 0.5f * dt;
            if (timer > u.corpseLifetime) Object.Destroy(u.gameObject);
            return Vector3.zero;
        }
    }
}
