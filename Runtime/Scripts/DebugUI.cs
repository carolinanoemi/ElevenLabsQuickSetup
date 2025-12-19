using System.Collections.Generic; 
using UnityEngine;
using TMPro;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class DebugUI : MonoBehaviour
{
    #region Inspector 

    [Header("UI References")]
    [Tooltip("Drag DebugPanel Object here")]
    [SerializeField] private GameObject debugPanel;
    [Tooltip("Drag Title Text here")]
    [SerializeField] private TextMeshProUGUI titleText;
    [Tooltip("Drag Log Text here")]
    [SerializeField] private TextMeshProUGUI logText;
    [Tooltip("Drag Transcript Text here")]
    [SerializeField] private TextMeshProUGUI transcriptText;

    [Header("Settings")]
    [Tooltip("Maximum number of log lines to keep in the debug panel.")]
    [SerializeField] private int maxDebugLines = 4; 
    [Tooltip("Maximum number of transcript lines to keep in the debug panel.")]
    [SerializeField] private int maxTranscriptLines = 2; 

#if ENABLE_INPUT_SYSTEM
    [Header("Input System (New)")]
    [Tooltip("Drag a Button-type InputActionReference here (e.g. ToggleDebug bound to F3).")]
    [SerializeField] private InputActionReference toggleDebugAction;
#else
    [Header("Input (Old - Input Manager)")]
    [Tooltip("Only used if Project Settings is set to: Active Input Handling includes Input Manager (Old).")]
    [SerializeField] private KeyCode toggleKey = KeyCode.F3;
#endif

    #endregion

    #region Internal Properties

    public static DebugUI Instance { get; private set; }

    // Use Queues for FIFO (First In, First Out) logic
    private readonly Queue<string> _logQueue = new Queue<string>();
    private readonly Queue<string> _transcriptQueue = new Queue<string>();

    // Backing fields for properties so we can detect changes
    private string _connectionState = "Unknown";
    private string _micState = "Idle";
    private string _lastEvent = "-";
    private float _lastVadScore = 0f;

    // Public Properties that automatically update the UI when set
    public string ConnectionState
    {
        get => _connectionState;
        set { _connectionState = value; UpdateHeaderUI(); }
    }

    public string MicState
    {
        get => _micState;
        set { _micState = value; UpdateHeaderUI(); }
    }

    public string LastEvent
    {
        get => _lastEvent;
        set { _lastEvent = value; UpdateLogUI(); }
    }

    public float LastVadScore
    {
        get => _lastVadScore;
        set { _lastVadScore = value; UpdateLogUI(); }
    }

    #endregion

    #region Unity Runtime

    private void Awake()
    {
        // Singleton pattern so other scripts can call DebugUI.Log(...)
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // Initialize UI once on startup
        UpdateHeaderUI();
        UpdateLogUI();
    }

#if ENABLE_INPUT_SYSTEM
    private void OnEnable()
    {
        // New Input System:
        // We subscribe to an InputAction event (no Update() polling).
        // This is best for packages because it's clear and avoids per-frame input checks.
        if (toggleDebugAction == null)
        {
            // Not an error — just means the user hasn't wired the action yet.
            // (They can still open the panel via code or set it active in the scene.)
            return;
        }

        toggleDebugAction.action.Enable();
        toggleDebugAction.action.performed += OnToggleDebugPerformed;
    }

    private void OnDisable()
    {
        if (toggleDebugAction == null) return;

        toggleDebugAction.action.performed -= OnToggleDebugPerformed;
        toggleDebugAction.action.Disable();
    }

    // Called when the ToggleDebug input action is performed (e.g. when pressing F3)
    private void OnToggleDebugPerformed(InputAction.CallbackContext ctx)
    {
        TogglePanel();
    }
#else
    private void Update()
    {
        // Old Input Manager fallback (only used if ENABLE_INPUT_SYSTEM is NOT defined)
        if (Input.GetKeyDown(toggleKey))
        {
            TogglePanel();
        }
    }
#endif

    #endregion

    #region Private UI Methods

    // Small helper so both input paths (new/old) can toggle the panel the same way.
    private void TogglePanel()
    {
        if (debugPanel == null) return;
        debugPanel.SetActive(!debugPanel.activeSelf);
    }

    // Only called when Connection or Mic state changes
    private void UpdateHeaderUI()
    {
        if (titleText == null) return;
        titleText.text = $"ElevenLabs Debug | WS: {_connectionState} | Mic: {_micState}";
    }

    // Only called when VAD, Event, or Logs change
    private void UpdateLogUI()
    {
        if (logText == null) return;

        // Use string.Join to combine the queue.
        string logHistory = string.Join("\n", _logQueue);

        logText.text = $"Last VAD: {_lastVadScore:F2}\n" +
                       $"Last Event: {_lastEvent}\n\n" +
                       $"<b>Log:</b>\n{logHistory}";
    }

    // Only called when someone speaks
    private void UpdateTranscriptUI()
    {
        if (transcriptText == null) return;
        transcriptText.text = string.Join("\n", _transcriptQueue);
    }

    #endregion

    #region Public Static Methods

    public static void Log(string message)
    {
        // Fail loud: If Instance is missing, let Unity throw the NullRef so we know to fix it.
        Instance.AddLogInternal(message);
    }

    public static void UserSaid(string text)
    {
        Instance.AddTranscriptInternal("<color=#FFB6FF><b>You</b></color>", text);
    }

    public static void AgentSaid(string text)
    {
        Instance.AddTranscriptInternal("<color=#B6E1FF><b>Glacier</b></color>", text);
    }

    #endregion

    #region Internal Logic

    private void AddLogInternal(string message)
    {
        _logQueue.Enqueue($"[{Time.time:0.0}s] {message}");

        while (_logQueue.Count > maxDebugLines)
        {
            _logQueue.Dequeue();
        }

        UpdateLogUI();
    }

    private void AddTranscriptInternal(string speaker, string text)
    {
        _transcriptQueue.Enqueue($"{speaker}: {text}");

        while (_transcriptQueue.Count > maxTranscriptLines)
        {
            _transcriptQueue.Dequeue();
        }

        UpdateTranscriptUI();
    }

    #endregion
}
