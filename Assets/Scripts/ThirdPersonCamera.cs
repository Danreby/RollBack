using UnityEngine;

public class ThirdPersonCamera : MonoBehaviour
{
    public Transform target;
    public float distance = 6f;
    public float sensitivity = 3f;

    private float yaw;
    private float pitch = 10f;
    private ThirdPersonController targetController;

    private void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void LateUpdate()
    {
        if (target == null)
            return;

        if (targetController == null)
            targetController = target.GetComponent<ThirdPersonController>();

        yaw   -= Input.GetAxis("Mouse X") * sensitivity;
        pitch -= Input.GetAxis("Mouse Y") * sensitivity;
        pitch  = Mathf.Clamp(pitch, -84f, 84f);

        Vector3 combatOffset = targetController != null ? targetController.GetCombatCameraOffset() : Vector3.zero;
        Vector3 pivotPoint = target.position + combatOffset + Vector3.up * 1.5f;

        float pitchRad = pitch * Mathf.Deg2Rad;
        float yawRad   = yaw   * Mathf.Deg2Rad;

        Vector3 offset = new Vector3(
            Mathf.Cos(pitchRad) * Mathf.Sin(yawRad),
            Mathf.Sin(pitchRad),
            Mathf.Cos(pitchRad) * -Mathf.Cos(yawRad)
        ) * distance;

        Vector3 desiredPosition = pivotPoint + offset;

        if (Physics.Raycast(desiredPosition + Vector3.up * 50f, Vector3.down, out RaycastHit hit, 100f))
        {
            float groundY = hit.point.y + 0.1f;
            if (desiredPosition.y < groundY)
                desiredPosition.y = groundY;
        }

        transform.position = desiredPosition;

        Vector3 lookDir = pivotPoint - transform.position;
        Vector3 worldUp = Mathf.Abs(pitch) > 85f
            ? Quaternion.Euler(0f, yaw, 0f) * (pitch > 0 ? Vector3.back : Vector3.forward)
            : Vector3.up;

        transform.rotation = Quaternion.LookRotation(lookDir, worldUp);
    }
}
