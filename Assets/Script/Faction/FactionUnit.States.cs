using UnityEngine;

/// <summary>
/// FactionUnit의 개인 FSM 상태들.
/// 각 상태는 Tick에서 ① 전이 조건을 검사하고 ② 이번 프레임의 보이드 조향 힘을 돌려준다.
/// 진영 단위 전이(평온↔경계↔전투)는 FactionCamp가 OnCampCombat/OnCampCalm 등으로 걸어준다.
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

    // ───────── 평소: 역할 행동 ─────────
    class IdleState : State
    {
        public IdleState(FactionUnit u) : base(u) { }
        public override string Name => "Idle";
        public override Color GizmoColor => Color.green;
        public override float Speed => u.walkSpeed;

        public override Vector3 Tick(float dt)
        {
            float s = Speed;

            if (u.role == Role.Guard)
            {
                // 경비: 동료와 겹치지 않을 정도의 분리 + 자기 자리로 돌아가는 힘
                u.Flock(u.separationRadius, s, out var sep, out _, out _);
                return sep * u.idleWeights.separation + u.Arrive(u.postPosition, s, 2f) * 1.5f;
            }

            var wp = u.camp != null ? u.camp.GroupWaypoint : null;
            if (wp.HasValue)
            {
                // 배회조 + 순찰 경로: 무리 전체가 같은 지점을 향하니 정렬/응집이 대열을 만든다
                var pw = u.patrolWeights;
                return u.Flocked(pw, s) + u.Arrive(wp.Value, s, 4f) * pw.goal;
            }

            // 배회조: 진영 안에서 무리 지어 어슬렁거림
            var w = u.idleWeights;
            return u.Flocked(w, s)
                 + u.HomeTether(s) * w.goal
                 + u.Wander(s, dt) * w.wander
                 - u.Velocity * u.idleDamping;
        }
    }

    // ───────── 조사: 수상한 지점에 가서 둘러봄 ─────────
    class InvestigateState : State
    {
        float lookTimer;

        public InvestigateState(FactionUnit u) : base(u) { }
        public override string Name => "Investigate";
        public override Color GizmoColor => Color.yellow;
        public override float Speed => u.investigateSpeed;

        public override void Enter() => Restart();
        public void Restart() => lookTimer = 0f;

        public override Vector3 Tick(float dt)
        {
            if (u.camp == null || u.camp.State == CampState.Calm) { u.ChangeState(u.returning); return Vector3.zero; }

            float s = Speed;
            Vector3 point = u.camp.InvestigatePoint;
            u.Flock(u.separationRadius, s, out var sep, out _, out _);

            if (Flat(point - u.transform.position).magnitude > 1.5f)
                return sep * 1.5f + u.Arrive(point, s, 2f) * 1.5f;

            // 도착: 천천히 맴돌며 두리번거림
            lookTimer += dt;
            if (lookTimer >= u.lookAroundTime) { u.ChangeState(u.returning); return Vector3.zero; }
            return sep * 1.5f + u.Wander(s * 0.6f, dt) + u.Arrive(point, s * 0.3f, 2f) * 0.5f;
        }
    }

    // ───────── 교전: 접근, 슬롯이 없으면 주위에서 순번 대기 ─────────
    class EngageState : State
    {
        float retargetTimer;

        public EngageState(FactionUnit u) : base(u) { }
        public override string Name => "Engage";
        public override Color GizmoColor => new Color(1f, 0.6f, 0f);
        public override float Speed => u.chaseSpeed;

        public override void Enter() => retargetTimer = 0f;

        public override Vector3 Tick(float dt)
        {
            retargetTimer -= dt;
            if (retargetTimer <= 0f || u.Target == null || !u.Target.IsAlive)
            {
                retargetTimer = u.scanInterval;
                u.Target = u.camp != null ? u.camp.BestTarget(u.transform.position, u.crowdPenalty, u.Target) : null;
            }

            float s = Speed;
            var t = u.Target;
            if (t == null)
            {
                // 진영이 아는 적이 없음 → 진영이 곧 경계/평온으로 바꿔줌. 그동안 무리 유지
                u.Flock(u.separationRadius, s, out var sep0, out _, out _);
                return sep0 - u.Velocity * u.combatDamping;
            }

            float reach = u.Reach(t);
            float d = u.DistanceTo(t);
            bool canTakeSlot = Time.time >= u.nextSlotTime && u.camp.HasFreeSlot(t);

            if (canTakeSlot && d <= reach) { u.ChangeState(u.attack); return Vector3.zero; }

            // 슬롯이 없고 가까우면: 한 걸음 뒤 원 위에서 서성이며 빈자리를 기다림
            if (!canTakeSlot && d < reach + u.waitRingOffset + 3f)
            {
                u.Flock(u.combatSeparationRadius, s, out var sep, out _, out _);
                return sep * u.combatSeparationWeight
                     + u.KeepDistance(t, reach + u.waitRingOffset, s) * 2f
                     + u.Sway(t, s, dt) * u.swayWeight
                     - u.Velocity * u.combatDamping;
            }

            // 멀면: 무리 지어 추격
            var w = u.chaseWeights;
            return u.Flocked(w, s) + u.SteerTowards(t.Position - u.transform.position, s) * w.goal;
        }
    }

    // ───────── 공격: 슬롯을 쥐고 붙어서 때림 ─────────
    class AttackState : State
    {
        FactionBody locked; // 들어올 때 잡은 표적. 슬롯을 정확히 반납하기 위해 따로 보관
        int hits;

        public AttackState(FactionUnit u) : base(u) { }
        public override string Name => "Attack";
        public override Color GizmoColor => Color.red;
        public override float Speed => u.attackMoveSpeed;

        public override void Enter()
        {
            locked = u.Target;
            hits = 0;
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
            if (locked == null || !locked.IsAlive) { u.ChangeState(u.engage); return Vector3.zero; }

            float s = Speed;
            float reach = u.Reach(locked);
            if (u.DistanceTo(locked) > reach * 1.4f) { u.ChangeState(u.engage); return Vector3.zero; }

            if (u.TryAttack(locked) && ++hits >= u.attacksPerTurn)
            {
                // 차례 끝: 슬롯을 양보하고 잠깐 물러남
                u.nextSlotTime = Time.time + u.slotCooldown;
                u.ChangeState(u.engage);
                return Vector3.zero;
            }

            // 응집은 끄고 넓은 분리 + 거리 유지 → 표적 주위에 원형 포위가 저절로 생김
            u.Flock(u.combatSeparationRadius, s, out var sep, out var ali, out _);
            return sep * u.combatSeparationWeight
                 + ali * 0.3f
                 + u.KeepDistance(locked, reach * 0.8f, s) * 2f
                 - u.Velocity * u.combatDamping;
        }
    }

    // ───────── 복귀: 제자리로, 가는 동안 회복 ─────────
    class ReturnState : State
    {
        public ReturnState(FactionUnit u) : base(u) { }
        public override string Name => "Return";
        public override Color GizmoColor => Color.gray;
        public override float Speed => u.returnSpeed;

        public override void Enter()
        {
            u.Target = null;
            u.Awareness = 0f;
            u.reportedSuspicion = false;
        }

        public override Vector3 Tick(float dt)
        {
            u.Body.Heal(u.returnHealPerSecond * dt);
            if (u.DistanceToPost < 1f) { u.ChangeState(u.idle); return Vector3.zero; }

            float s = Speed;
            var w = u.returnWeights;
            return u.Flocked(w, s) + u.Arrive(u.postPosition, s, 3f) * w.goal;
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
