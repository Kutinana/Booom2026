using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using QFramework;
using UnityEngine;

public class AudioMng : MonoBehaviour
{

    public Transform Player;
    public AudioSource BGM;
    private const int FootstepClipCount = 2;
    private const string SfxConfigPath = "sfx_config";

    private static AudioMng m_Instance;

    [SerializeField] private AudioClip[] m_FootstepClips = new AudioClip[FootstepClipCount];
    [SerializeField, Min(0f)] private float m_FootstepVolumeScale = 1f;

    private AudioSource m_AudioSource;
    private Coroutine m_BgmFadeCoroutine;
    private int m_FootstepIndex;
    private readonly Queue<SfxRequest> m_SfxQueue = new Queue<SfxRequest>();
    private readonly Dictionary<string, float> m_SfxLastPlayTimes = new Dictionary<string, float>();
    private readonly Dictionary<string, float> m_SfxCooldowns = new Dictionary<string, float>();
    private readonly Dictionary<string, AudioClip> m_SfxClipCache = new Dictionary<string, AudioClip>();

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
        LoadSfxConfig();
    }

    void Start()
    {
        QFramework.TypeEventSystem.Global.Register<CollectiveStarCollectedEvent>(e =>
        {
            PlaySfx("Star", 1f);
        }).UnRegisterWhenGameObjectDestroyed(this);
    }

    private void OnDestroy()
    {
        if (m_Instance == this)
        {
            m_Instance = null;
        }
    }

    private void Update()
    {
        while (m_SfxQueue.Count > 0)
        {
            SfxRequest request = m_SfxQueue.Dequeue();
            TryPlayQueuedSfx(request);
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

        m_SfxQueue.Enqueue(new SfxRequest(name, volumeScale));
    }

    public void FadeInBGM(float duration = 1f)
    {
        FadeBGMVolume(0.45f, duration);
    }

    public void FadeOutBGM(float duration = 1f)
    {
        FadeBGMVolume(0f, duration);
    }

    public void FadeBGMVolume(float targetVolume, float duration = 1f)
    {
        if (BGM == null)
        {
#if UNITY_EDITOR
            Debug.LogWarning("BGM AudioSource is not assigned.", this);
#endif
            return;
        }

        targetVolume = Mathf.Clamp01(targetVolume);

        if (m_BgmFadeCoroutine != null)
        {
            StopCoroutine(m_BgmFadeCoroutine);
        }

        if (duration <= 0f)
        {
            BGM.volume = targetVolume;
            m_BgmFadeCoroutine = null;
            return;
        }

        m_BgmFadeCoroutine = StartCoroutine(FadeBGMVolumeRoutine(targetVolume, duration));
    }

    private IEnumerator FadeBGMVolumeRoutine(float targetVolume, float duration)
    {
        if (BGM == null)
        {
            m_BgmFadeCoroutine = null;
            yield break;
        }

        float startVolume = BGM.volume;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            if (BGM == null)
            {
                m_BgmFadeCoroutine = null;
                yield break;
            }

            elapsed += Time.deltaTime;
            BGM.volume = Mathf.Lerp(startVolume, targetVolume, elapsed / duration);
            yield return null;
        }

        if (BGM != null)
        {
            BGM.volume = targetVolume;
        }

        m_BgmFadeCoroutine = null;
    }

    public void PlaySfxWithDecay(string name, float volumeScale, Vector3 pos, float distScale)
    {
        if (Player == null) 
        {
            PlaySfx(name, volumeScale); 
            return;
        }
        
        float sqrDist = (Player.position - pos).sqrMagnitude;
        float sqrScale = distScale * distScale;
        float decayedVolume = sqrDist > sqrScale ? volumeScale  / (sqrDist * sqrScale) : volumeScale;
        decayedVolume = Mathf.Clamp(decayedVolume, 0, volumeScale);
        PlaySfx(name, decayedVolume);
#if UNITY_EDITOR
        Debug.Log($"Play {name} volume {decayedVolume}");
#endif
    }

    private void TryPlayQueuedSfx(SfxRequest request)
    {
        if (IsSfxCoolingDown(request.Name))
        {
            return;
        }

        if (!m_SfxClipCache.TryGetValue(request.Name, out AudioClip clip))
        {
            clip = Resources.Load<AudioClip>(request.Name);
            if (clip != null)
            {
                m_SfxClipCache[request.Name] = clip;
            }
        }

        if (clip == null)
        {
#if UNITY_EDITOR
            Debug.LogWarning($"SFX not found in Resources: {request.Name}", this);
#endif
            return;
        }

        m_SfxLastPlayTimes[request.Name] = Time.time;
        PlayClip(clip, request.VolumeScale);
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

        m_AudioSource = gameObject.AddComponent<AudioSource>();
        m_AudioSource.playOnAwake = false;
    }

    private bool IsSfxCoolingDown(string name)
    {
        if (!m_SfxCooldowns.TryGetValue(name, out float cooldownSeconds) || cooldownSeconds <= 0f)
        {
            return false;
        }

        return m_SfxLastPlayTimes.TryGetValue(name, out float lastPlayTime)
            && Time.time - lastPlayTime < cooldownSeconds;
    }

    private void LoadSfxConfig()
    {
        m_SfxCooldowns.Clear();

        TextAsset config = Resources.Load<TextAsset>(SfxConfigPath);
        if (config == null)
        {
#if UNITY_EDITOR
            Debug.LogWarning("SFX config not found in Resources: sfx_config.csv", this);
#endif
            return;
        }

        string[] lines = config.text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line[0] == '#')
            {
                continue;
            }

            string[] columns = line.Split(',');
            if (columns.Length < 2)
            {
#if UNITY_EDITOR
                Debug.LogWarning($"Invalid SFX config row: {line}", this);
#endif
                continue;
            }

            string name = columns[0].Trim().Trim('\uFEFF');
            if (string.Equals(name, "name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (!float.TryParse(columns[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float cooldownSeconds))
            {
#if UNITY_EDITOR
                Debug.LogWarning($"Invalid SFX cooldown value: {line}", this);
#endif
                continue;
            }

            if (cooldownSeconds <= 0f)
            {
                continue;
            }

            m_SfxCooldowns[name] = cooldownSeconds;
#if UNITY_EDITOR
            Debug.Log($"Loaded SFX cooldown config: {name} = {cooldownSeconds}s");
#endif
        }
    }

    private readonly struct SfxRequest
    {
        public readonly string Name;
        public readonly float VolumeScale;

        public SfxRequest(string name, float volumeScale)
        {
            Name = name;
            VolumeScale = volumeScale;
        }
    }
}
