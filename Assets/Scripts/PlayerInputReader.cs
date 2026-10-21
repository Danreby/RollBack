using UnityEngine;

// Lê teclado e mouse (Input Manager clássico) num lugar só. Os outros scripts
// consultam estas propriedades em vez de chamar Input diretamente, o que facilita
// trocar as teclas, migrar para o Input System novo ou simular input em testes.
[DefaultExecutionOrder(-100)]
public class PlayerInputReader : MonoBehaviour
{
    public KeyCode jumpKey     = KeyCode.Space;
    public KeyCode sprintKey   = KeyCode.LeftShift;
    public KeyCode dodgeKey    = KeyCode.LeftControl;
    public KeyCode interactKey = KeyCode.E;
    public KeyCode dropKey     = KeyCode.G;
    public int attackMouseButton = 0;
    [Tooltip("Botão do mouse alternativo para esquivar (-1 = nenhum)")]
    public int dodgeMouseButton = 1;

    [Tooltip("Desligue para controlar o personagem por código (ex.: testes automatizados)")]
    public bool readHardware = true;

    public Vector2 Move { get; set; }
    public bool SprintHeld { get; set; }
    public bool JumpPressed { get; set; }
    public bool AttackPressed { get; set; }
    public bool DodgePressed { get; set; }
    public bool InteractPressed { get; set; }
    public bool DropPressed { get; set; }

    private void Update()
    {
        if (!readHardware)
            return;

        Vector2 move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        Move = move.sqrMagnitude > 1f ? move.normalized : move;

        // Com o cursor solto (Esc) o clique serve só para recapturar o mouse.
        bool mouseCaptured = Cursor.lockState == CursorLockMode.Locked;

        SprintHeld      = Input.GetKey(sprintKey) || Input.GetKey(KeyCode.RightShift);
        JumpPressed     = Input.GetKeyDown(jumpKey);
        AttackPressed   = mouseCaptured && Input.GetMouseButtonDown(attackMouseButton);
        DodgePressed    = Input.GetKeyDown(dodgeKey) || (mouseCaptured && dodgeMouseButton >= 0 && Input.GetMouseButtonDown(dodgeMouseButton));
        InteractPressed = Input.GetKeyDown(interactKey);
        DropPressed     = Input.GetKeyDown(dropKey);
    }

    private void LateUpdate()
    {
        // Os "pressed" valem por um frame só, também quando vêm de código.
        JumpPressed     = false;
        AttackPressed   = false;
        DodgePressed    = false;
        InteractPressed = false;
        DropPressed     = false;
    }
}
