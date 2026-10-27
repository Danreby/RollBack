using UnityEngine;

// Lê teclado e mouse (Input Manager clássico) num lugar só. Os outros scripts
// consultam estas propriedades em vez de chamar Input diretamente, o que facilita
// trocar as teclas, migrar para o Input System novo ou simular input em testes.
[DefaultExecutionOrder(-100)]
public class PlayerInputReader : MonoBehaviour
{
    public KeyCode jumpKey     = KeyCode.Space;
    public KeyCode sprintKey   = KeyCode.LeftShift;
    [Tooltip("Segurado: anda em vez de correr")]
    public KeyCode walkKey     = KeyCode.LeftAlt;
    [Tooltip("Alterna entre andar e correr")]
    public KeyCode walkToggleKey = KeyCode.CapsLock;
    public KeyCode dodgeKey    = KeyCode.LeftControl;
    [Tooltip("Pegar arma do chão")]
    public KeyCode interactKey = KeyCode.F;
    public KeyCode dropKey     = KeyCode.G;
    [Tooltip("Trava/destrava a mira no inimigo")]
    public KeyCode lockOnKey   = KeyCode.E;
    [Tooltip("Com a mira travada: passa para o próximo inimigo")]
    public KeyCode switchTargetKey = KeyCode.Tab;
    [Tooltip("Lançador: joga o inimigo para o alto")]
    public KeyCode launcherKey = KeyCode.Q;
    public int attackMouseButton = 0;
    [Tooltip("Golpe pesado")]
    public int heavyMouseButton = 1;
    [Tooltip("Botão do mouse alternativo para esquivar (-1 = nenhum)")]
    public int dodgeMouseButton = -1;

    [Tooltip("Desligue para controlar o personagem por código (ex.: testes automatizados)")]
    public bool readHardware = true;

    public Vector2 Move { get; set; }
    // Movimento do mouse neste frame (troca de alvo com um "puxão" lateral, com a mira travada)
    public Vector2 Look { get; set; }
    public bool SprintHeld { get; set; }
    public bool WalkHeld { get; set; }
    public bool WalkTogglePressed { get; set; }
    public bool JumpPressed { get; set; }
    public bool JumpHeld { get; set; }
    public bool AttackPressed { get; set; }
    public bool HeavyPressed { get; set; }
    public bool LauncherPressed { get; set; }
    public bool DodgePressed { get; set; }
    public bool InteractPressed { get; set; }
    public bool DropPressed { get; set; }
    public bool LockOnPressed { get; set; }
    public bool SwitchTargetPressed { get; set; }
    // Tecla numérica apertada neste frame (1 = punhos, 2..9 = armas); 0 = nenhuma
    public int WeaponSlotPressed { get; set; }

    private void Update()
    {
        if (!readHardware)
            return;

        Vector2 move = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        Move = move.sqrMagnitude > 1f ? move.normalized : move;

        // Com o cursor solto (Esc) o clique serve só para recapturar o mouse.
        bool mouseCaptured = Cursor.lockState == CursorLockMode.Locked;
        Look = mouseCaptured ? new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) : Vector2.zero;

        SprintHeld      = Input.GetKey(sprintKey) || Input.GetKey(KeyCode.RightShift);
        WalkHeld        = Input.GetKey(walkKey);
        WalkTogglePressed = Input.GetKeyDown(walkToggleKey);
        JumpPressed     = Input.GetKeyDown(jumpKey);
        JumpHeld        = Input.GetKey(jumpKey);
        AttackPressed   = mouseCaptured && Input.GetMouseButtonDown(attackMouseButton);
        HeavyPressed    = mouseCaptured && heavyMouseButton >= 0 && Input.GetMouseButtonDown(heavyMouseButton);
        LauncherPressed = Input.GetKeyDown(launcherKey);
        DodgePressed    = Input.GetKeyDown(dodgeKey) || (mouseCaptured && dodgeMouseButton >= 0 && Input.GetMouseButtonDown(dodgeMouseButton));
        InteractPressed = Input.GetKeyDown(interactKey);
        DropPressed     = Input.GetKeyDown(dropKey);
        LockOnPressed   = Input.GetKeyDown(lockOnKey) || Input.GetMouseButtonDown(2);
        SwitchTargetPressed = Input.GetKeyDown(switchTargetKey);

        WeaponSlotPressed = 0;
        for (int i = 1; i <= 9; i++)
            if (Input.GetKeyDown(KeyCode.Alpha0 + i) || Input.GetKeyDown(KeyCode.Keypad0 + i))
                WeaponSlotPressed = i;
    }

    private void LateUpdate()
    {
        // Os "pressed" valem por um frame só, também quando vêm de código.
        JumpPressed     = false;
        AttackPressed   = false;
        HeavyPressed    = false;
        LauncherPressed = false;
        DodgePressed    = false;
        InteractPressed = false;
        DropPressed     = false;
        LockOnPressed   = false;
        SwitchTargetPressed = false;
        WalkTogglePressed = false;
        WeaponSlotPressed = 0;
    }
}
