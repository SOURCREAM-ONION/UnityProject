using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(CharacterController))]
public class PlayerMovement : MonoBehaviour
{
    [Header("MovementSetting")]
    public float walkSpeed = 3f;        // 걷기 속도
    public float runSpeed = 7f;         // 달리기 속도
    public float rotationSpeed = 10f;   // 캐릭터가 도는 속도

    [Header("Gravity Setting")]
    public float gravity = -9.81f;      // 중력 값
    public Vector3 velocity;            // 수직 속도 계산용

    [Header("Animation Setting")]
    public float speedSmoothTime = 0.1f; // 걷기/달리기 전환이 부드럽게 이어지는 시간
    private float speedSmoothVelocity;
    private float currentAnimationSpeed;

    // 컴포넌트 변수들
    private CharacterController controller;
    private Vector2 moveInput;
    private bool isSprinting;
    private Transform cameraTransform;
    private Animator animator;

    void Start()
    {
        controller = GetComponent<CharacterController>();
        animator = GetComponent<Animator>();

        if (Camera.main != null)
        {
            cameraTransform = Camera.main.transform;
        }
    }

    void OnMove(InputValue value)
    {
        moveInput = value.Get<Vector2>();
    }

    void OnSprint(InputValue value)
    {
        isSprinting = value.isPressed;
    }
    void Update()
    {
        // 1. 카메라가 바라보는 방향을 기준으로 이동 방향 계산
        Vector3 camForward = cameraTransform.forward;
        Vector3 camRight = cameraTransform.right;

        camForward.y = 0;
        camRight.y = 0;
        camForward.Normalize();
        camRight.Normalize();

        Vector3 moveDirection = (camForward * moveInput.y) + (camRight * moveInput.x);

        // 2. 공격 상태 감지 (철벽 방어 로직)
        bool isAttacking = false;
        if (animator != null)
        {
            AnimatorStateInfo currentInfo = animator.GetCurrentAnimatorStateInfo(0);
            AnimatorStateInfo nextInfo = animator.GetNextAnimatorStateInfo(0);
            bool isTransitioning = animator.IsInTransition(0);

            // 현재 재생 중인 상태가 "Attack" 태그이거나, 넘어갈 다음 상태가 "Attack"이라면 공격 중으로 간주
            if (currentInfo.IsTag("Attack") || (isTransitioning && nextInfo.IsTag("Attack")))
            {
                isAttacking = true;
            }
        }

        // 3. 캐릭터 몸통 회전 (입력값이 있고, 공격 중이 아닐 때만)
        if (moveDirection != Vector3.zero && !isAttacking)
        {
            Quaternion targetRotation = Quaternion.LookRotation(moveDirection);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);
        }

        // 4. 이동 속도 결정
        float targetMoveSpeed = isSprinting ? runSpeed : walkSpeed;
        if (moveInput == Vector2.zero) targetMoveSpeed = 0f;

        // 공격 중이라면 강제로 이동 속도와 방향을 0으로 차단
        if (isAttacking)
        {
            targetMoveSpeed = 0f;
            moveDirection = Vector3.zero;
        }

        // 5. 수평 이동 적용 (공격 중이 아닐 때만 허용)
        if (!isAttacking)
        {
            controller.Move(moveDirection * targetMoveSpeed * Time.deltaTime);
        }

        // 6. 중력 처리 (수직 이동은 공격 중에도 적용되어야 공중에 뜨지 않음)
        if (controller.isGrounded && velocity.y < 0)
        {
            velocity.y = -2f; // 바닥에 착 붙도록 약간의 힘을 줌
        }
        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);

        // 7. 애니메이션 파라미터 (Blend Tree) 업데이트
        float targetAnimationSpeed = 0f;
        if (moveInput != Vector2.zero && !isAttacking)
        {
            // 걷기는 0.5, 달리기는 1
            targetAnimationSpeed = isSprinting ? 1f : 0.5f;
        }

        if (animator != null)
        {
            // 0, 0.5, 1 사이의 값을 부드럽게 스무딩하여 애니메이터에 전달
            currentAnimationSpeed = Mathf.SmoothDamp(currentAnimationSpeed, targetAnimationSpeed, ref speedSmoothVelocity, speedSmoothTime);
            animator.SetFloat("Speed", currentAnimationSpeed);
        }
    }
}
