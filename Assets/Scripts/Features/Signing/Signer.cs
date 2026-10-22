using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Core.SceneManagement;
using Features.Gameplay.Entities.Enemy;
using Features.Gameplay.Entities.Player;
using Features.Signing.Recognition;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Features.Signing
{
    public class Signer : MonoBehaviour
    {
        [Header("Sign Recognition")]
        [Tooltip("Any component that implements ISignRecognizer (e.g. DevKeySignRecognizer). " +
                 "If left empty, Signer uses one on this same GameObject.")]
        [SerializeField] private MonoBehaviour recognizerSource;

        [Header("Plug in")]
        public WordBank wordBank;
        public Text inferenceText;
        public Text confidenceScoreText;
        public Image background;

        [SerializeField] private SessionSelection sessionSelection;

        [Header("Win")]
        [SerializeField] private string winSceneName = "WinScene";
        [SerializeField] private float winDelaySeconds = 2f;

        [Header("Player Data")]
        [SerializeField] private PlayerHealth playerHealth;

        // internals
        private ISignRecognizer _recognizer;
        private bool _subscribed;
        private SceneBindings _bindings;
        private readonly List<string> _filterWords = new();

        // A destroyed MonoBehaviour still looks non-null through an interface reference,
        // so check it the Unity way when the recognizer is a Unity object.
        private bool HasRecognizer => _recognizer is Object unityObject ? unityObject != null : _recognizer != null;

        private void OnValidate()
        {
            if (recognizerSource && recognizerSource is not ISignRecognizer)
            {
                Debug.LogWarning($"{name}: {recognizerSource.GetType().Name} does not implement ISignRecognizer.", this);
                recognizerSource = null;
            }
        }

        private void Awake()
        {
            if (background) background.color = Color.black;

            var recognizer = recognizerSource as ISignRecognizer ?? GetComponent<ISignRecognizer>();
            if (recognizer == null)
                Debug.LogWarning($"{name}: no sign recognizer found. Add a DevKeySignRecognizer (or another ISignRecognizer) to this GameObject.", this);

            BindRecognizer(recognizer);
        }

        private void Start()
        {
            StopSigning(classify: false);           // make sure we start idle
            ApplyRecognizerFilterToDictionaryWords(); // one-time setup (used to be polled every frame in Update)
            StartCoroutine(AssignEnemyLabelsWhenReady());
        }

        private void Update()
        {
            UserSigning();
        }

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            SubscribeToRecognizer();
        }

        private void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            UnsubscribeFromRecognizer();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _bindings = FindFirstObjectByType<SceneBindings>(FindObjectsInactive.Include);

            if (!_bindings)
            {
                _filterWords.Clear();
                ApplyRecognizerFilterToDictionaryWords();
                return;
            }

            wordBank            = _bindings.wordBank;
            inferenceText       = _bindings.inferenceText;
            confidenceScoreText = _bindings.condifenceScoreText;
            background          = _bindings.background;

            // Only switch recognizers if this scene provides one.
            if (_bindings.Recognizer != null)
                BindRecognizer(_bindings.Recognizer);

            if (background) background.color = Color.black;

            ApplyRecognizerFilterToDictionaryWords();

            StartCoroutine(AssignEnemyLabelsWhenReady());
        }

        // ---------- recognizer wiring ----------

        private void BindRecognizer(ISignRecognizer recognizer)
        {
            if (ReferenceEquals(recognizer, _recognizer)) return;

            var wasSubscribed = _subscribed;
            UnsubscribeFromRecognizer();

            _recognizer = recognizer;

            if (wasSubscribed) SubscribeToRecognizer();
            PushVocabulary();
        }

        private void SubscribeToRecognizer()
        {
            if (_subscribed || !HasRecognizer) return;
            _recognizer.SignRecognized += OnSignRecognized;
            _subscribed = true;
        }

        private void UnsubscribeFromRecognizer()
        {
            if (_subscribed && _recognizer != null)
                _recognizer.SignRecognized -= OnSignRecognized;
            _subscribed = false;
        }

        /// <summary>Tell the recognizer which words are currently in play.</summary>
        private void PushVocabulary()
        {
            if (HasRecognizer) _recognizer.SetVocabulary(_filterWords);
        }

        // ---------- enemy words ----------

        public void RefreshEnemyLabels()
        {
            StartCoroutine(AssignEnemyLabelsWhenReady());
        }

        private IEnumerator AssignEnemyLabelsWhenReady()
        {
            yield return null;

            var enemyLabels = FindObjectsByType<EnemyLabel>(FindObjectsSortMode.None);
            if (enemyLabels == null || enemyLabels.Length == 0) yield break;

            List<string> chosen;
            if (sessionSelection && sessionSelection.HasWords)
            {
                chosen = new List<string>(sessionSelection.Words)
                    .Take(enemyLabels.Length)
                    .Select(w => (w ?? "").Trim().ToLowerInvariant())
                    .ToList();
            }
            else if (wordBank)
            {
                chosen = wordBank.GetRandomWords(enemyLabels.Length, unique: true);
            }
            else
            {
                chosen = new List<string>();
            }

            _filterWords.Clear();
            _filterWords.AddRange(chosen);
            ApplyRecognizerFilterToDictionaryWords(); // unchanged from before: dictionary words win when a recognizer is present

            for (int i = 0; i < enemyLabels.Length && i < _filterWords.Count; i++)
                SafeSetEnemyWord(enemyLabels[i], _filterWords[i]);
        }

        private static void SafeSetEnemyWord(EnemyLabel label, string word)
        {
            if (!label || !label.label) return;
            label.SetWord(word);
        }

        // ---------- signing input ----------

        private void UserSigning()
        {
            if (GameModeState.HintTypingModeActive)
                return;

            if (Input.GetKeyDown(KeyCode.Return)) StartSigning();
            if (Input.GetKeyUp(KeyCode.Return))   StopSigning(classify: true);
        }

        public void BeginMobileSign()
        {
            if (GameModeState.HintTypingModeActive)
                return;

            StartSigning();
        }

        public void EndMobileSign()
        {
            if (GameModeState.HintTypingModeActive)
                return;

            StopSigning(classify: true);
        }

        private void StartSigning()
        {
            if (background) background.color = Color.white;
            if (HasRecognizer) _recognizer.BeginCapture();
        }

        /// <param name="classify">True: recognize what was signed. False: throw it away.</param>
        private void StopSigning(bool classify)
        {
            if (HasRecognizer)
            {
                if (classify) _recognizer.EndCapture();
                else _recognizer.CancelCapture();
            }

            if (background) background.color = Color.black;
        }

        // ---------- results ----------

        private void OnSignRecognized(string rawInput, float confidenceScore)
        {
            if (GameModeState.HintTypingModeActive)
                return;

            var signed = (rawInput ?? "").Trim().ToLowerInvariant();
            var normalizedScore = Mathf.RoundToInt(confidenceScore * 100f);
            if (string.IsNullOrEmpty(signed))
            {
                SetInferenceTextFields(signed, normalizedScore, Color.red);
                return;
            }

            var labels = FindObjectsByType<EnemyLabel>(FindObjectsSortMode.None);
            EnemyLabel match = null;
            foreach (var label in labels)
            {
                if (!label) continue;
                if (string.Equals(label.targetWord, signed, System.StringComparison.OrdinalIgnoreCase))
                {
                    match = label;
                    break;
                }
            }

            if (!match)
            {
                SetInferenceTextFields(signed, normalizedScore, Color.red);
                if (playerHealth) playerHealth.Damage(1);
                return;
            }

            var enemyController = match.GetComponentInParent<EnemyController>() ?? match.GetComponent<EnemyController>();
            var bossController  = match.GetComponentInParent<TutorialBossController>() ?? match.GetComponent<TutorialBossController>();

            if (enemyController)
            {
                enemyController.Explode();
                SetInferenceTextFields(signed, normalizedScore, Color.green);
                RemoveWordFromList(signed);
                StartCoroutine(CheckForWinNextFrame());
            }
            else if (bossController)
            {
                bossController.HandleSignedWord(match, 1);
                SetInferenceTextFields(signed, normalizedScore, Color.green);
            }
            else
            {
                Destroy(match.gameObject);
                SetInferenceTextFields(signed, normalizedScore, Color.green);
                RemoveWordFromList(signed);
                StartCoroutine(CheckForWinNextFrame());
            }
        }

        private void SetInferenceTextFields(string signed, int score, Color textColor)
        {
            if (inferenceText)
            {
                inferenceText.text = signed;
                inferenceText.color = textColor;
                if (confidenceScoreText && score >= 0f && score <= 100)
                {
                    confidenceScoreText.text = score.ToString() + "%";
                    confidenceScoreText.color = textColor;
                }
            }
        }

        private void RemoveWordFromList(string word)
        {
            if (string.IsNullOrEmpty(word)) return;
            var key = word.Trim().ToLowerInvariant();

            for (int i = _filterWords.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_filterWords[i], key, System.StringComparison.OrdinalIgnoreCase))
                    _filterWords.RemoveAt(i);
            }

            if (sessionSelection != null && sessionSelection.words != null)
            {
                for (int i = sessionSelection.words.Count - 1; i >= 0; i--)
                {
                    var s = sessionSelection.words[i];
                    if (string.Equals(s?.Trim().ToLowerInvariant(), key, System.StringComparison.OrdinalIgnoreCase))
                        sessionSelection.words.RemoveAt(i);
                }
            }

            PushVocabulary();
        }

        // ---------- win ----------

        private IEnumerator CheckForWinNextFrame()
        {
            yield return null;

            bool noWordsLeft = _filterWords == null || _filterWords.Count == 0;

            if (noWordsLeft)
                TriggerWin();
        }

        private void TriggerWin()
        {
            if (HasRecognizer) _recognizer.CancelCapture();
            if (background) background.color = Color.black;
            StartCoroutine(LoadWinAfterDelay());
        }

        private IEnumerator LoadWinAfterDelay()
        {
            yield return new WaitForSeconds(winDelaySeconds);
            if (!string.IsNullOrEmpty(winSceneName))
                SceneManager.LoadScene(winSceneName, LoadSceneMode.Single);
        }

        public void HandleEnemyKilled(EnemyLabel label)
        {
            if (GameModeState.HintTypingModeActive)
                return;

            if (label && !string.IsNullOrEmpty(label.targetWord))
                RemoveWordFromList(label.targetWord.ToLowerInvariant());

            StartCoroutine(CheckForWinNextFrame());
        }

        // ---------- dictionary filter ----------

        private List<string> GetDictionaryWords()
        {
            if (sessionSelection != null && sessionSelection.HasWords && sessionSelection.Words != null)
                return Normalize(sessionSelection.Words);

            return new List<string>();
        }

        private static List<string> Normalize(IEnumerable<string> raw)
        {
            if (raw == null) return new List<string>();
            var list = new List<string>();
            foreach (var w in raw)
            {
                var s = (w ?? "").Trim().ToLowerInvariant();
                if (!string.IsNullOrEmpty(s) && !list.Contains(s))
                    list.Add(s);
            }
            return list;
        }

        private void ApplyRecognizerFilterToDictionaryWords()
        {
            if (!HasRecognizer) return;

            _filterWords.Clear();
            _filterWords.AddRange(GetDictionaryWords());
            PushVocabulary();
        }
    }
}
