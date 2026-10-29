using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerAttack : MonoBehaviour
{
    private Animator animator;

    void Start()
    {
        animator = GetComponentInChildren<Animator>();
    }

    void OnAttack(InputValue value)
    {
        if (value.isPressed)
        {
            // 클릭하면 일단 방아쇠를 당김
            animator.SetTrigger("Attack");
        }
    }

    // 🌟 [핵심 수정 2] 매 프레임 감시하면서 불필요한 선입력을 칼같이 잘라냅니다.
    void Update()
    {
        if (animator == null) return;

        AnimatorStateInfo currentInfo = animator.GetCurrentAnimatorStateInfo(0);
        AnimatorStateInfo nextInfo = animator.GetNextAnimatorStateInfo(0);
        bool isTransitioning = animator.IsInTransition(0);

        // 방어 1: 막타(Attack3) 애니메이션이 재생 중일 때는 들어온 입력을 모두 삭제합니다.
        if (currentInfo.IsName("Attack3"))
        {
            animator.ResetTrigger("Attack");
        }

        // 방어 2: 공격을 멈추고 걷기/대기(Blend Tree)로 돌아가는 찰나의 순간에도 입력을 삭제합니다.
        // (현재는 공격 태그인데, 넘어갈 다음 상태가 공격 태그가 아닌 경우)
        if (isTransitioning && currentInfo.IsTag("Attack") && !nextInfo.IsTag("Attack"))
        {
            animator.ResetTrigger("Attack");
        }
    }
}