using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class VoiceInputManager : MonoBehaviour
{
    [Header("Audio Input")]
    [Tooltip("Target sample rate for sending audio to ElevenLabs. If in doubt see output log.")]
    public int sampleRate = 48000; 
    [Tooltip("Microphone device name. Leave empty for default device.")]
    public string microphoneDevice = "";
    [Tooltip("Length of the mic recording buffer in seconds.")]
    public int micBufferLengthSeconds = 10; // Increased to 10s for safety
    [Tooltip("Number of samples per audio chunk to send (at target sample rate).")]
    public int sendChunkSize = 1600;

    [Header("Audio Processing")]
    [Tooltip("Multiplier to boost mic volume. Raw Unity mic is often too quiet.")]
    public float inputGain = 3.0f;

    [Tooltip("Debug: If RMS is below this, we log a warning.")]
    public float silenceThreshold = 0.001f;

    [Header("Debug")]
    [Tooltip("Enable debug logging for mic input.")]
    public bool logDebug = true;

    // Private fields

    private AudioClip _micClip;
    private bool _micRunning = false;
    private int _lastMicSamplePos = 0;
    private bool _connected = false;

    private bool _isTalking = false;
    private List<float> _accumulator = new List<float>();
    private float[] _readBuffer;

    public bool IsMicRunning => _micRunning;
    public bool IsTalking => _isTalking;

  
    // Fired whenever a ready-to-send audio chunk is available (Base64 PCM16 at target sampleRate).
    // ElevenlabsAgent listens and sends it over the WebSocket.
    public event Action<string> OnAudioChunkReady;

    private void Awake()
    {
       
        if (Microphone.IsRecording(microphoneDevice))
        {
            Debug.LogWarning("[ElevenLabs] Found lingering mic session from previous run. Forcing stop.");
            Microphone.End(microphoneDevice);
        }
    }

    public void SetConnected(bool value)
    {
        _connected = value;
    }

    #region MIC START / STOP

    public void StartMic()
    {
        // Prevent multiple starts by checking if already running
        if (_micRunning) return;
        if (logDebug) Debug.Log("[ElevenLabs] Starting microphone...");

        // Start recording from the microphone and create AudioClip
        Microphone.GetDeviceCaps(microphoneDevice, out int minFreq, out int maxFreq);
        int actualSampleRate = maxFreq > 0 ? maxFreq : 44100; // Default to 44100 if unknown

        _micClip = Microphone.Start(microphoneDevice, true, micBufferLengthSeconds, actualSampleRate);

        if (_micClip == null)
        {
            Debug.LogError("[ElevenLabs] Failed to start microphone.");
            return;
        }

        // Recalculate chunk size based on ACTUAL freq
        sendChunkSize = Mathf.CeilToInt(_micClip.frequency * 0.1f);

        _micRunning = true;

        // Start the MicSendLoop
        StartCoroutine(MicSendLoop());
    }

    public void StopMic()
    {
        _micRunning = false;
        if (Microphone.IsRecording(microphoneDevice))
        {
            Microphone.End(microphoneDevice);
        }
    }

    #endregion

    #region TALK STATE HOOKS (CALLED BY AGENT)

    
    public void StartTalking()
    {
        _accumulator.Clear();

        // make sure we start reading from the current mic position and never old data or outside the array
        int currentMicPos = Microphone.GetPosition(microphoneDevice);
        if (_micClip != null && _micClip.samples > 0)
        {
            // handle negative wrap-around just in case
            if (currentMicPos < 0) currentMicPos = 0;
            currentMicPos = currentMicPos % _micClip.samples;
        }
        else
        {
            currentMicPos = 0;
        }

        // set last position to current to avoid old data
        _lastMicSamplePos = currentMicPos;
        _isTalking = true;
    }

    public void SetTalking(bool value)
    {
        _isTalking = value;
    }

    public void ClearAccumulator()
    {
        _accumulator.Clear();
    }

    #endregion

    #region MAIN MIC LOOP 

    private IEnumerator MicSendLoop()
    {
        // wait until the microphone has started recording
        while (_micRunning && Microphone.GetPosition(microphoneDevice) <= 0)
            yield return null;

        if (logDebug && _micClip != null)
            Debug.Log($"[ElevenLabs] MicSendLoop started at {_micClip.frequency}Hz.");

        // Clear accumulator at start so we don't send old data
        _accumulator.Clear();

        // Main loop
        while (_micRunning && _connected)
        {
            // if we're not talking, just update last position and wait for next frame
            if (!_isTalking)
            {
                int pos = Microphone.GetPosition(microphoneDevice);
                if (pos >= 0)
                    _lastMicSamplePos = pos; // avoid old data by updating last position

                yield return null;
                continue;
            }

            int currentPos = Microphone.GetPosition(microphoneDevice);

            // safety check for invalid position
            if (currentPos < 0 || _micClip == null)
            {
                yield return null;
                continue;
            }

            // check how many samples are available to read
            // how much sound have we recorded since last read position
            int samplesAvailable;
            if (currentPos >= _lastMicSamplePos)
            {
                samplesAvailable = currentPos - _lastMicSamplePos;
            }
            else
            {
                // we reached the end of the clip and wrapped around
                samplesAvailable = (_micClip.samples - _lastMicSamplePos) + currentPos;
            }

            // read available samples in chunks to avoid invalid parameter crash
            if (samplesAvailable > 0)
            {
                // safe guard to not read more than buffer length
                int remaining = samplesAvailable;
                int readPos = _lastMicSamplePos;

                // read in chunks until we have read all available samples
                while (remaining > 0)
                {
                    // read the smaller of remaining chunks or until end of clip only
                    int chunk = Mathf.Min(remaining, _micClip.samples - readPos);

                    // only allocate read buffer if needed
                    if (_readBuffer == null || _readBuffer.Length != chunk)
                        _readBuffer = new float[chunk];

                    // ask for data from mic clip
                    _micClip.GetData(_readBuffer, readPos);

                    // copy to accumulator with gain applied
                    for (int i = 0; i < chunk; i++)
                        _accumulator.Add(_readBuffer[i] * inputGain);

                    remaining -= chunk;
                    // update read position with wrap-around
                    readPos = (readPos + chunk) % _micClip.samples;
                }

                _lastMicSamplePos = currentPos;
            }

            // send the data in fixed-size chunks to elevenlabs when we have enough accumulated
            while (_accumulator.Count >= sendChunkSize)
            {
                float[] chunk = _accumulator.GetRange(0, sendChunkSize).ToArray();
                _accumulator.RemoveRange(0, sendChunkSize);

                float rms = CalculateRMS(chunk);
                if (rms < silenceThreshold && logDebug)
                {
                    // Debug.LogWarning($"[ElevenLabs] Low Audio RMS: {rms:F5}");
                }

                ResampleAndSend(chunk);
            }

            yield return null;
        }

        if (logDebug) Debug.Log("[ElevenLabs] MicSendLoop ended.");
    }

    #endregion

    #region SAMPLE PROCESSING (RMS, RESAMPLE, PCM16)

    // Calculate RMS of audio samples for volume analysis
    private float CalculateRMS(float[] samples)
    {
        float sum = 0;
        foreach (var s in samples) sum += s * s;
        return Mathf.Sqrt(sum / samples.Length);
    }

    // Resample input samples to target sample rate and raise event with base64
    private void ResampleAndSend(float[] inputSamples)
    {
        // 1. Direct send if matches sample rate
        if (_micClip != null && _micClip.frequency == sampleRate)
        {
            byte[] pcmBytes = FloatsToPcm16(inputSamples, inputSamples.Length);
            string b64 = Convert.ToBase64String(pcmBytes);

            OnAudioChunkReady?.Invoke(b64);
            return;
        }

        if (_micClip == null) return;

        // 2. Downsample if needed
        float ratio = (float)_micClip.frequency / (float)sampleRate;
        int newLength = Mathf.FloorToInt(inputSamples.Length / ratio);
        float[] resampled = new float[newLength];

        for (int i = 0; i < newLength; i++)
        {
            int originalIndex = (int)(i * ratio);
            if (originalIndex < inputSamples.Length)
                resampled[i] = inputSamples[originalIndex];
        }

        // 3. Convert to PCM16 and send
        byte[] finalBytes = FloatsToPcm16(resampled, resampled.Length);
        if (finalBytes.Length > 0)
        {
            string finalB64 = Convert.ToBase64String(finalBytes);
            OnAudioChunkReady?.Invoke(finalB64);
        }
    }

    // Convert float samples (-1.0 to 1.0) to PCM16 byte array
    // Clamps values to avoid overflow
    public byte[] FloatsToPcm16(float[] samples, int sampleCount)
    {
        if (sampleCount > samples.Length) sampleCount = samples.Length;
        byte[] bytes = new byte[sampleCount * 2];
        for (int i = 0; i < sampleCount; i++)
        {
            float f = Mathf.Clamp(samples[i], -1f, 1f);
            short s = (short)Mathf.RoundToInt(f * 32767f);
            bytes[i * 2] = (byte)(s & 0xFF);
            bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return bytes;
    }

    #endregion
}
