using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// AudioOutputManager
// -----------------
// What this script does:
// - Takes raw PCM16 audio bytes (the format ElevenLabs streams back)
// - Converts the bytes into Unity float samples (-1..1)
// - Creates an AudioClip from those samples
// - Plays clips one after another (queue) so chunks don't overlap
//
// Why we need a queue:
// - ElevenLabs audio arrives in small chunks.
// - If we play each chunk immediately, they can overlap / cut each other off.
// - A queue makes playback smooth and in order.

[RequireComponent(typeof(AudioSource))]
public class AudioOutputManager : MonoBehaviour
{
    

    [Header("Debug")]
    [Tooltip("Enable debug logging for audio playback.")]
    public bool logDebug = true;

  
    #region Private Fields

    // Queue holds audio clips in the order we receive them (FIFO = first in, first out).
    private readonly Queue<AudioClip> _audioQueue = new Queue<AudioClip>();

    // Simple lock so we don't start multiple playback coroutines at the same time.
    private bool _isPlayingQueue = false;

    // The AudioSource that actually plays sound in Unity.
    private AudioSource _audioSource;

    #endregion

    #region Unity Runtime

    private void Awake()
    {

        _audioSource = GetComponent<AudioSource>();

        // We want 3D/spatial sound (so the voice feels like it comes from the character).
        // 0 = 2D, 1 = fully 3D.
        _audioSource.spatialBlend = 1f;

        // Safety: don't play anything automatically when the scene starts.
        _audioSource.playOnAwake = false;
    }

    #endregion

    #region Public API

    // PlayPcm16
    // ---------
    // pcmBytes: raw audio bytes in PCM16 little-endian format
    // rate: sample rate (e.g. 16000 or 48000 depending on what the agent uses)
    //
    // What we do:
    // 1) Convert bytes -> short samples (16-bit)
    // 2) Convert short -> float samples (-1..1), which Unity uses
    // 3) Create an AudioClip
    // 4) Enqueue it for sequential playback
    public void PlayPcm16(byte[] pcmBytes, int rate)
    {
        // If we receive nothing, do nothing.
        if (pcmBytes == null || pcmBytes.Length == 0) return;

        // PCM16 means: 2 bytes per sample (16-bit).
        int sampleCount = pcmBytes.Length / 2;

        // Unity audio clips store sample data as floats between -1 and 1.
        float[] samples = new float[sampleCount];

        // Convert little-endian PCM16 bytes into float samples.
        // - Bytes are ordered: [lowByte, highByte]
        // - Combine them to a signed 16-bit number (short)
        // - Normalize to float by dividing by 32768
        for (int i = 0; i < sampleCount; i++)
        {
            short s = (short)(pcmBytes[i * 2] | (pcmBytes[i * 2 + 1] << 8));
            samples[i] = s / 32768f;
        }

        // Create a Unity AudioClip from the samples.
        // - "ElevenLabsChunk": name (doesn't matter, helps in debugging)
        // - sampleCount: total samples
        // - 1 channel: mono (voice)
        // - rate: sample rate given by the stream
        // - false: no streaming from disk (we already have all samples in memory)
        AudioClip clip = AudioClip.Create("ElevenLabsChunk", sampleCount, 1, rate, false);

        // Copy sample data into the clip.
        clip.SetData(samples, 0);

        // Ensure playback settings are correct (voice chunks should not loop).
        _audioSource.loop = false;

        // Keep it 3D. (In case another script changes AudioSource settings.)
        _audioSource.spatialBlend = 1f;

        // Add the clip to the queue.
        _audioQueue.Enqueue(clip);

        if (logDebug)
            Debug.Log($"[ElevenLabs] Enqueued audio chunk. {clip.length:F2}s");

        // If we're not already playing, start the playback coroutine.
        // This ensures only ONE coroutine is managing the queue at any time.
        if (!_isPlayingQueue)
            StartCoroutine(PlayAudioQueue());
    }

    #endregion

    #region Playback Coroutine

    // Plays queued clips one-by-one.
    // Why coroutine: - It lets us wait until the current clip is done before playing the next.
    private IEnumerator PlayAudioQueue()
    {
        _isPlayingQueue = true;

        while (_audioQueue.Count > 0)
        {
            // Grab the next clip.
            AudioClip next = _audioQueue.Dequeue();

            // Play it.
            _audioSource.clip = next;
            _audioSource.Play();

            // Wait until the clip finishes before continuing.
            // We add a tiny padding to reduce the risk of tiny gaps/cutoffs between chunks.
            yield return new WaitForSeconds(next.length + 0.05f);
        }

        _isPlayingQueue = false;
    }

    #endregion
}
