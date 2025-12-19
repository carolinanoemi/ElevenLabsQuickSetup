using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using NativeWebSocket;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif


/// Main controller for ElevenLabs real-time conversational AI agent.
/// Manages WebSocket communication, message routing, and push-to-talk function.
/// Delegates audio capture to VoiceInputManager and playback to AudioOutputManager.


public class ElevenLabsAgent : MonoBehaviour
{
    #region Keys & Inspector

    [Header("Keys")]
    [Tooltip("Your ElevenLabs API Key")]
    public string apiKey;
    [Tooltip("The Agent ID to connect to.")]
    public string agentId;

    [Header("Push To Talk")]
    [Tooltip("Key to hold for push-to-talk.")]
    public KeyCode pushToTalkKey = KeyCode.T;

#if ENABLE_INPUT_SYSTEM
[Tooltip("New Input System: Button action for Push-To-Talk (started=down, canceled=up).")]
public InputActionReference pushToTalkAction;
#endif


    [Header("Debug")]
    [Tooltip("If true, logs debug messages to console.")]
    public bool logDebug = true;

    [Tooltip("Minimum time (seconds) button must be held. Prevents micro-clicks.")]
    public float minTalkDuration = 0.5f;

    [Header("Connection")]
    [Tooltip("If true, sends periodic keep-alive messages while the player is in range.")]
    public bool enableKeepAlive = true;

    [Tooltip("Seconds between keep-alive messages while idle.")]
    public float keepAliveIntervalSeconds = 15f;


    [Tooltip("How much trailing silence to send after releasing PTT (seconds).")]
    public float trailingSilenceSeconds = 1f; // 


    [Header("Trigger Settings")]
    [Tooltip("Tag of the player object to detect entering/exiting trigger.")]
    public string playerTag = "Player";

    [Header("Managers")]
    public VoiceInputManager voiceInputManager;
    public AudioOutputManager audioOutputManager;


    // Fired when agent sends a text response. Used for displaying transcript or logging conversation
    // Consumers: IntentRouter, UI, logging etc
    public event Action<string> OnAgentText;

    #endregion

    #region private fields

    private WebSocket _ws;

    // Connection and conversation flags.
    private bool _connected = false;
    private bool _inConversation = false;
    private bool _conversationInitiated = false;
    private bool _readyForTalk = false;

    // Tracks last received audio base64 payload to skip duplicate playback
    private string _lastAudioBase64 = null;

    // For keeping the connection alive while the player is in range
    private bool _playerInsideTrigger = false;
    private bool _isTryingReconnect = false;
    private Coroutine _keepAliveCoroutine;


    // Tracks how long the PTT key has been held in the current press to avoid micro-clicks.
    private float _currentPressDuration = 0f;
    // Used to temporarily block new input while we send final audio/silence to prevent overlapping inputs.
    private bool _isProcessing = false;

#if ENABLE_INPUT_SYSTEM
private bool _pttHeld = false;
#endif


    #endregion

    #region JSON classes

    // Small containers matching the ElevenLabs server's JSON events used with JsonUtility.FromJson.
    // Kept private and minimal to reduce parsing overhead and clearly document expected fields.
    [Serializable] private class AudioEventWrapper { public string type; public AudioEvent audio_event; }
    [Serializable] private class AudioEvent { public string audio_base_64; public int event_id; }
    [Serializable] private class PingEvent { public string type; public PingPayload ping_event; }
    [Serializable] private class PingPayload { public int event_id; public int ping_ms; }
    [Serializable] private class AgentResponseWrapper { public string type; public AgentResponseEvent agent_response_event; }
    [Serializable] private class AgentResponseEvent { public string agent_response; }
    [Serializable] private class UserTranscriptionWrapper { public string type; public UserTranscriptionEvent user_transcription_event; }
    [Serializable] private class UserTranscriptionEvent { public string user_transcript; public int event_id; }

    #endregion

    #region Unity Runtime Methods

    private void Start()
    {
        DebugUI.Log("Scene started – debug UI is working.");
    }

    private void Awake()
    {
        // Auto fetch components if not assigned in inspector
        if (voiceInputManager == null)
            voiceInputManager = GetComponent<VoiceInputManager>();

        if (audioOutputManager == null)
            audioOutputManager = GetComponent<AudioOutputManager>();

        if (voiceInputManager == null)
            Debug.LogError("[ElevenLabs] VoiceInputManager component missing!");

        if (audioOutputManager == null)
            Debug.LogError("[ElevenLabs] AudioOutputManager component missing!");

        // Subscribe to mic audio chunks so we can forward to ElevenLabs WS
        if (voiceInputManager != null)
        {
            // When mic has a ready chunk, send it as user_audio_chunk
            voiceInputManager.OnAudioChunkReady += HandleMicChunkReady;
        }
    }

    private void Update()
    {
#if !UNITY_WEBGL || UNITY_EDITOR

        // Ensure the WebSocket's internal message queue is processed on the main thread.
        // NativeWebSocket requires DispatchMessageQueue to be called from the main thread in Unity.
        if (_ws != null) _ws.DispatchMessageQueue();
#endif

        
        // Push-to-talk function that uses voiceInputManager logic
        if (_connected && _readyForTalk && !_isProcessing)
        {
#if ENABLE_INPUT_SYSTEM
    // New Input System path:
    // We don't use GetKeyDown/GetKeyUp here. The action events handle down/up.
    // Here we only track "held duration" while talking (micro-click prevention).
    if (_pttHeld && voiceInputManager != null && voiceInputManager.IsTalking)
    {
        _currentPressDuration += Time.deltaTime;
    }

#else
            // Old Input Manager path (legacy):
            if (Input.GetKeyDown(pushToTalkKey))
            {
                _currentPressDuration = 0f;
                StartTalking();
            }

            if (Input.GetKey(pushToTalkKey) && voiceInputManager != null && voiceInputManager.IsTalking)
            {
                _currentPressDuration += Time.deltaTime;
            }

            if (Input.GetKeyUp(pushToTalkKey))
            {
                if (_currentPressDuration < minTalkDuration) CancelTalking();
                else StopTalking();
            }
#endif
        }

    }


    private void OnDestroy()
    {
        StopAllCoroutines();
        if (voiceInputManager != null)
            voiceInputManager.OnAudioChunkReady -= HandleMicChunkReady;

        Cleanup();
    }

    // centralized cleanup to prevent locks and to ensure mic and network state are synchronized.
    private void Cleanup()
    {

        if (_keepAliveCoroutine != null)
        {
            StopCoroutine(_keepAliveCoroutine);
            _keepAliveCoroutine = null;
        }

        if (voiceInputManager != null)
        {
            voiceInputManager.StopMic();
            voiceInputManager.SetConnected(false);
            voiceInputManager.SetTalking(false);
            voiceInputManager.ClearAccumulator();
        }

        CloseWebSocket();
    }


    #endregion

    #region INPUT SYSTEM HANDLING
    private void OnEnable()
    {
#if ENABLE_INPUT_SYSTEM
    if (pushToTalkAction != null)
    {
        pushToTalkAction.action.Enable();
        pushToTalkAction.action.started += OnPttStarted;
        pushToTalkAction.action.canceled += OnPttCanceled;
    }
#endif
    }

    private void OnDisable()
    {
#if ENABLE_INPUT_SYSTEM
    if (pushToTalkAction != null)
    {
        pushToTalkAction.action.started -= OnPttStarted;
        pushToTalkAction.action.canceled -= OnPttCanceled;
        pushToTalkAction.action.Disable();
    }
#endif
    }

#if ENABLE_INPUT_SYSTEM
private void OnPttStarted(InputAction.CallbackContext ctx)
{
    // Match old GetKeyDown behavior
    if (!_connected || !_readyForTalk || _isProcessing) return;

    _currentPressDuration = 0f;
    _pttHeld = true;
    StartTalking();
}

#if ENABLE_INPUT_SYSTEM
private void OnPttCanceled(InputAction.CallbackContext ctx)
{
    // Match old GetKeyUp behavior
    if (!_pttHeld) return;

    _pttHeld = false;

    if (_currentPressDuration < minTalkDuration) CancelTalking();
    else StopTalking();
}
#endif
#endif
    #endregion


    #region TRIGGER HANDLING

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag)) return;

        _playerInsideTrigger = true;

        if (logDebug) Debug.Log("[ElevenLabs] Player entered trigger, starting conversation...");
        if (!_connected) StartCoroutine(StartConversationFlow());
    }

    private void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag(playerTag)) return;

        _playerInsideTrigger = false;

        if (logDebug) Debug.Log("[ElevenLabs] Player left trigger, stopping conversation...");
        _inConversation = false;
        Cleanup();
    }

    #endregion

    #region CONVERSATION FLOW

    // Main conversation flow coroutine
    //  Used a coroutine here to keep the connection lifecycle asynchronous and compatible with Unity's update loop.
    private IEnumerator StartConversationFlow()
    {
        if (string.IsNullOrEmpty(agentId))
        {
            Debug.LogError("[ElevenLabs] agentId is empty!");
            yield break;
        }
        yield return ConnectWebSocket();
    }

    #endregion

    #region WEBSOCKET

    // Connect to ElevenLabs WebSocket and wire event callbacks.
    private IEnumerator ConnectWebSocket()
    {
        string url = $"wss://api.elevenlabs.io/v1/convai/conversation?agent_id={agentId}";
        if (logDebug) Debug.Log("[ElevenLabs] Connecting to " + url);
        DebugUI.Log("Opening WebSocket...");
        DebugUI.Instance.ConnectionState = "Connecting";

  
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(apiKey))
        {
            headers.Add("xi-api-key", apiKey);
        }

        _ws = new WebSocket(url, headers);

        // When open, notify voiceInputManager and send conversationInitiation and start mic
        _ws.OnOpen += () =>
        {
            if (logDebug) Debug.Log("[ElevenLabs] WebSocket open.");
            _connected = true;
            if (voiceInputManager != null)
                voiceInputManager.SetConnected(true);

            DebugUI.Log("WebSocket connected.");
            DebugUI.Instance.ConnectionState = "Open";

            SendConversationInitiation();

            // Start the mic only after the connection is open so we don't capture audio before the server can receive it
            if (voiceInputManager != null)
                voiceInputManager.StartMic();

            // Start keep-alive coroutine if enabled
            if (enableKeepAlive && _keepAliveCoroutine == null)
            {
                if (logDebug) DebugUI.Log("Starting keep-alive loop...");
                _keepAliveCoroutine = StartCoroutine(KeepAliveLoop());
            }
        };

        // When closed, ensure state is cleared so future attempts can reconnect cleanly
        _ws.OnClose += (NativeWebSocket.WebSocketCloseCode code) =>
        {
            _connected = false;
            if (voiceInputManager != null)
                voiceInputManager.SetConnected(false);

            DebugUI.Log("WebSocket closed.");
            DebugUI.Instance.ConnectionState = "Closed";
            if (logDebug) Debug.Log($"[ElevenLabs] WebSocket closed, code: {code}");

            if (_keepAliveCoroutine != null)
            {
                StopCoroutine(_keepAliveCoroutine);
                _keepAliveCoroutine = null;
            }
        };

        _ws.OnError += (string errMsg) =>
        {
            Debug.LogError("[ElevenLabs] WebSocket error: " + errMsg);
            DebugUI.Log("WebSocket ERROR: " + errMsg);
            DebugUI.Instance.ConnectionState = "Error";
        };

        // Route incoming bytes to our message handler which implements the protocol logic
        _ws.OnMessage += OnWebSocketMessage;

        var connectTask = _ws.Connect();
        while (!connectTask.IsCompleted) yield return null;
    }

    private void CloseWebSocket()
    {

        if (_ws != null)
        {
            if (logDebug) Debug.Log("[ElevenLabs] Closing WebSocket...");
            _ws.Close();
            _ws = null;
            _connected = false;
            if (voiceInputManager != null)
                voiceInputManager.SetConnected(false);
        }
    }

    // Handles all incoming WebSocket messages from ElevenLabs server
    // We parse only the fields we need, skip duplicates, and route events to appropriate subsystems(UI, audio, transcription).
    private void OnWebSocketMessage(byte[] bytes)
    {
        // Decode message
        string msg = Encoding.UTF8.GetString(bytes);


        if (msg.Contains("\"type\":\"error\"") || msg.Contains("\"error_event\""))
        {
            Debug.LogError("[ElevenLabs] ERROR from server: " + msg);
            return;
        }


        if (msg.Contains("\"type\":\"user_transcript\""))
        {
            try
            {
                // Parse user transcription to extract text
                UserTranscriptionWrapper ut = JsonUtility.FromJson<UserTranscriptionWrapper>(msg);
                if (ut != null && ut.user_transcription_event != null)
                {
                    string text = ut.user_transcription_event.user_transcript;
                    if (logDebug) Debug.Log($"[ElevenLabs] user_transcript: \"{text}\"");
                    DebugUI.UserSaid(text);

                    // Clearing last audio helps ensure we don't skip agent audio that follows this transcript.
                    _lastAudioBase64 = null;
                }
            }
            catch (Exception ex) { Debug.LogWarning("[ElevenLabs] Failed to parse user_transcript: " + ex.Message); }
        }

        // Metadata handling
        if (msg.Contains("\"type\":\"conversation_initiation_metadata\""))
        {
            Debug.Log("[ElevenLabs] INIT METADATA: " + msg);

            // Mark conversation as initiated and ready for talk
            if (_conversationInitiated) return;
            _conversationInitiated = true;
            _readyForTalk = true;
            if (logDebug) Debug.Log("[ElevenLabs] Metadata received. Mic Ready.");
            return;
        }

        // Ping handling
        if (msg.Contains("\"type\":\"ping\""))
        {
            try
            {
                // Reply with pong event
                PingEvent ping = JsonUtility.FromJson<PingEvent>(msg);
                QueueSend("{\"type\":\"pong\",\"event_id\":" + ping.ping_event.event_id + "}");
            }
            catch { }
            return;
        }

        // Audio handling
        if (msg.Contains("\"type\":\"audio\""))
        {
            try
            {
                // Parse audio event to extract base64 audio
                AudioEventWrapper wrapper = JsonUtility.FromJson<AudioEventWrapper>(msg);
                if (wrapper != null && wrapper.audio_event != null && !string.IsNullOrEmpty(wrapper.audio_event.audio_base_64))
                {
                    string b64 = wrapper.audio_event.audio_base_64;
                    if (_lastAudioBase64 == b64) return; // Skip duplicates

                    _lastAudioBase64 = b64;
                    byte[] audioBytes = Convert.FromBase64String(b64);

                    if (audioOutputManager != null && voiceInputManager != null)
                        audioOutputManager.PlayPcm16(audioBytes, voiceInputManager.sampleRate);
                }
            }
            catch (Exception ex) { Debug.LogWarning("[ElevenLabs] Failed to parse audio: " + ex.Message); }
        }

        // Agent Response handling
        if (msg.Contains("\"type\":\"agent_response\""))
        {
            try
            {
                // Parse agent response to extract text/transcript
                AgentResponseWrapper ar = JsonUtility.FromJson<AgentResponseWrapper>(msg);
                if (ar != null && ar.agent_response_event != null)
                {
                    _lastAudioBase64 = null;
                    string text = ar.agent_response_event.agent_response;
                    if (logDebug) Debug.Log("[ElevenLabs] agent_response: " + text);
                    DebugUI.AgentSaid(text);
                    RaiseAgentText(text);
                }
            }
            catch { }
        }
    }

    private void QueueSend(string json)
    {
        // send message if websocket is open
        if (_ws == null || _ws.State != WebSocketState.Open) return;
        _ws.SendText(json);
        if (logDebug) Debug.Log("[ElevenLabs] Sent: " + json);
    }

    private void SendConversationInitiation()
    {
        int targetRate = voiceInputManager != null ? voiceInputManager.sampleRate : 48000;

        string json =
            "{"
            + "\"type\":\"conversation_initiation_client_data\","
            + "\"conversation_config_override\":{"
                + "\"agent\":{"
                    + "\"language\":\"en\","
                    + "\"input_audio_format\":{"
                        + "\"sample_rate\":" + targetRate
                    + "}"
                + "},"
                + "\"tts\":{"
                    + "\"output_format\":\"pcm_" + targetRate + "\""
                + "}"
            + "}"
            + "}";

        QueueSend(json);
        if (logDebug) Debug.Log($"[ElevenLabs] Handshake Sent: Force Input/Output to {targetRate}Hz");
    }



    private void HandleMicChunkReady(string base64Audio)
    {
        
        QueueSend("{\"user_audio_chunk\":\"" + base64Audio + "\"}");
    }

    private IEnumerator KeepAliveLoop()
    {
        while (_connected)
        {
            // Only keep alive if the player is actually in the interaction zone.
            if (_playerInsideTrigger && enableKeepAlive)
            {
                // "user_activity" is a lightweight signal that doesn't create speech,
                // but counts as activity for the conversation.
                QueueSend("{\"type\":\"user_activity\"}");
            }

            yield return new WaitForSeconds(keepAliveIntervalSeconds);
        }

        _keepAliveCoroutine = null;
    }


    #endregion

    #region TALKING CONTROL

    private void StartTalking()
    {
        if (voiceInputManager == null) return;
        if (voiceInputManager.IsTalking || !_connected || !voiceInputManager.IsMicRunning) return;
        if (logDebug) Debug.Log("[ElevenLabs] PTT down.");

        voiceInputManager.StartTalking();
        _inConversation = true;
    }

    private async void StopTalking()
    {
        if (voiceInputManager == null) return;
        if (!voiceInputManager.IsTalking) return;

        // lock input to allow network to catch up 
        _isProcessing = true;
        voiceInputManager.SetTalking(false);

        if (logDebug) Debug.Log($"[ElevenLabs] PTT up ({_currentPressDuration:F2}s). Sending Stop.");

        // Send silence chunk
        try
        {
           

            // send trailing silence to help the server detect end of speech
            int silenceSamples = Mathf.RoundToInt(
                voiceInputManager.sampleRate * Mathf.Max(trailingSilenceSeconds, 0.8f));

            // create silence buffer
            float[] silenceBuffer = new float[silenceSamples];
            byte[] pcm = voiceInputManager.FloatsToPcm16(silenceBuffer, silenceSamples);
            string b64 = Convert.ToBase64String(pcm);
            QueueSend("{\"user_audio_chunk\":\"" + b64 + "\"}");
        }
        catch (Exception ex) { Debug.LogWarning("Error sending silence: " + ex.Message); }

        voiceInputManager.ClearAccumulator();

        // delay so the WebSocket can send the silence before we fully stop
        await System.Threading.Tasks.Task.Delay(250);

        _isProcessing = false;
        if (logDebug) Debug.Log("[ElevenLabs] PTT session ended.");
    }

    // Cancel talking method for too short inputs
    private void CancelTalking()
    {
        if (voiceInputManager == null) return;
        if (!voiceInputManager.IsTalking) return;
        if (logDebug) Debug.LogWarning($"[ElevenLabs] Input too short ({_currentPressDuration:F2}s). Cancelled.");
        voiceInputManager.SetTalking(false);
        voiceInputManager.ClearAccumulator();
    }

    #endregion

    #region AGENT TEXT EVENT


    void RaiseAgentText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        OnAgentText?.Invoke(text);
    }

    #endregion
}
