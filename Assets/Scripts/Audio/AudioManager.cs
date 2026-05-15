using System;
using QFramework;
using UnityEngine;

public class AudioMng : MonoBehaviour
{
    private const int FootstepClipCount = 2;

    private static AudioMng m_Instance;

    [SerializeField] private AudioClip[] m_FootstepClips = new AudioClip[FootstepClipCount];
    [SerializeField, Min(0f)] private float m_FootstepVolumeScale = 1f;

    private AudioSource m_AudioSource;
    private int m_FootstepIndex;

    public static AudioMng Instance
    {
        get
        {
            if (m_Instance != null)
            {
                return m_Instance;
            }

            m_Instance = FindFirstObjectByType<AudioMng>();
            if (m_Instance != null)
            {
                return m_Instance;
            }

            GameObject audioManager = new GameObject(nameof(AudioMng));
            m_Instance = audioManager.AddComponent<AudioMng>();
            return m_Instance;
        }
    }

    private void Awake()
    {
        if (m_Instance != null && m_Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        m_Instance = this;
        DontDestroyOnLoad(gameObject);
        EnsureAudioSource();
    }

    void Start()
    {
        QFramework.TypeEventSystem.Global.Register<CollectiveStarCollectedEvent>(e =>
        {
            PlaySfx("star", 1f);
        }).UnRegisterWhenGameObjectDestroyed(this);
    }

    private void OnDestroy()
    {
        if (m_Instance == this)
        {
            m_Instance = null;
        }
    }

    private void OnValidate()
    {
        if (m_FootstepClips == null || m_FootstepClips.Length != FootstepClipCount)
        {
            Array.Resize(ref m_FootstepClips, FootstepClipCount);
        }
    }

    public void PlaySfx(string name, float volumeScale)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        AudioClip clip = Resources.Load<AudioClip>(name);
        if (clip == null)
        {
            Debug.LogWarning($"SFX not found in Resources: {name}", this);
            return;
        }

        PlayClip(clip, volumeScale);
    }

    public void PlayFootstep()
    {
        if (m_FootstepClips == null || m_FootstepClips.Length != FootstepClipCount)
        {
            return;
        }

        AudioClip clip = m_FootstepClips[m_FootstepIndex];
        m_FootstepIndex = (m_FootstepIndex + 1) % FootstepClipCount;

        if (clip == null)
        {
            return;
        }

        PlayClip(clip, m_FootstepVolumeScale);
    }

    private void PlayClip(AudioClip clip, float volumeScale)
    {
        EnsureAudioSource();
        m_AudioSource.PlayOneShot(clip, volumeScale);
    }

    private void EnsureAudioSource()
    {
        if (m_AudioSource != null)
        {
            return;
        }

        m_AudioSource = GetComponent<AudioSource>();
        if (m_AudioSource == null)
        {
            m_AudioSource = gameObject.AddComponent<AudioSource>();
        }

        m_AudioSource.playOnAwake = false;
    }
}
