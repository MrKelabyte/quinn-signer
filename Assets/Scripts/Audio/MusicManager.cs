using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;

namespace Audio
{
    [RequireComponent(typeof(AudioSource))]
    public class MusicManager : MonoBehaviour
    {
        private static MusicManager Instance { get; set; }

        [Header("Music tracks")]
        public AudioClip titleTheme;
        public AudioClip tutorialTheme;
        public AudioClip glyphwayTheme;
        public AudioClip shopTheme;
        public AudioClip winTheme;
        public AudioClip gameOverTheme;

        [Tooltip("Multiplayer race music. One is picked at random each time RaceScene loads.")]
        public AudioClip[] raceThemes;

        private AudioSource _audioSource;

        private void Awake()
        {
            // enforce singleton & persistence
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
                _audioSource = GetComponent<AudioSource>();
                SceneManager.sceneLoaded += OnSceneLoaded;
            }
            else Destroy(gameObject);
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == "RaceScene" && raceThemes != null && raceThemes.Length > 0)
            {
                PlayTheme(raceThemes[Random.Range(0, raceThemes.Length)]);
                return;
            }

            // swap based on scene name (or buildIndex, tag, etc.)
            switch (scene.buildIndex)
            {
                case 0:
                    PlayTheme(titleTheme);
                    break;
                case 1:
                    PlayTheme(tutorialTheme);
                    break;
                case 2:
                    PlayTheme(glyphwayTheme);
                    break;
                case 3:
                    PlayTheme(shopTheme);
                    break;
                case 4:
                    PlayTheme(winTheme);
                    break;
                case 5:
                    PlayTheme(gameOverTheme);
                    break;
            }
        }

        private void PlayTheme(AudioClip clip)
        {
            if (clip == null) return;
            // if already playing this clip, do nothing
            if (_audioSource.clip == clip && _audioSource.isPlaying) return;
            _audioSource.clip = clip;
            _audioSource.Play();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }
    }
}
