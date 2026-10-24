using System;
using System.Collections.Generic;
using Features.Signing;
using Features.Signing.Recognition;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.Video;
using Object = UnityEngine.Object;

namespace Multiplayer
{
    /// <summary>
    /// The Sign Zone: when your ship reaches one, it auto-pilots while you answer 3 questions.
    /// More correct answers = a bigger, longer speed boost afterwards.
    ///
    /// Question types:
    /// - GUESS THE SIGN: a reference video plays; tap the matching word (or press 1-4 on PC).
    /// - SIGN IT: a word is shown with your live camera; hold the SIGN button (or Return), sign
    ///   it, release. Uses the SLRTk camera recognizer on phones. On platforms SLRTk doesn't
    ///   support (the Windows Editor) it falls back to dev keys: release = correct,
    ///   Shift+release = wrong.
    ///
    /// Runs entirely on the local device. Only the result (a boost and the stats) is networked.
    /// </summary>
    public class SignZoneQuiz
    {
        private enum QuestionType { GuessTheSign, SignIt }

        private class Question
        {
            public QuestionType Type;
            public string Answer;
            public string[] Choices;
        }

        private const int QuestionsPerZone = 3;
        private const float GuessSeconds = 9f;
        private const float SignSeconds = 10f;
        private const float CorrectPause = 0.8f;
        private const int SignItVocabularySize = 8; // answer + 7 other words, so the model has to really pick
        private const float WrongPause = 1.4f;

        private static readonly Color PanelColor = new Color(0.08f, 0.02f, 0.18f, 0.92f);
        private static readonly Color ZoneColor = new Color(0.65f, 0.3f, 1f, 1f);
        private static readonly Color ChoiceColor = new Color(0.25f, 0.15f, 0.45f, 1f);
        private static readonly Color Good = new Color(0.3f, 1f, 0.4f, 1f);
        private static readonly Color Bad = new Color(1f, 0.35f, 0.35f, 1f);

        private readonly GameObject _panel;
        private readonly TextMeshProUGUI _header, _prompt, _feedback;
        private readonly Image _timerFill;
        private readonly RawImage _videoImage;
        private readonly VideoPlayer _video;
        private readonly RenderTexture _videoTexture;
        private readonly Button[] _choices = new Button[4];
        private readonly TextMeshProUGUI[] _choiceLabels = new TextMeshProUGUI[4];
        private readonly GameObject _signButton;
        private readonly RawImage _cameraImage;
        private readonly RectTransform _videoRect;
        private readonly TextMeshProUGUI _signLabel;
        private float _captureStarted;

        private readonly List<Question> _questions = new List<Question>();
        private static List<string> s_words;

        private NetworkRacePlayer _player;
        private ISignRecognizer _recognizer;
        private int _index, _correct;
        private float _timeLeft, _pauseLeft;
        private bool _awaitingAnswer;
        private bool _capturing;

        public bool IsOpen => _panel.activeSelf;

        public SignZoneQuiz(RectTransform hudRoot)
        {
            var panel = UiFactory.Panel("SignZoneQuiz", hudRoot, PanelColor, raycast: true);
            panel.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -60f), new Vector2(1000f, 1300f));
            _panel = panel.gameObject;
            var t = panel.transform;

            _header = UiFactory.Text("Header", t, "SIGN ZONE", 64f, TextAlignmentOptions.Center, ZoneColor);
            _header.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -30f), new Vector2(960f, 90f));

            var timerBack = UiFactory.Panel("TimerBack", t, new Color(0f, 0f, 0f, 0.5f));
            timerBack.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -130f), new Vector2(900f, 16f));
            _timerFill = UiFactory.Panel("TimerFill", timerBack.transform, ZoneColor);
            var fr = _timerFill.rectTransform;
            fr.anchorMin = Vector2.zero; fr.anchorMax = Vector2.one; fr.offsetMin = fr.offsetMax = Vector2.zero; fr.pivot = new Vector2(0f, 0.5f);

            _videoTexture = new RenderTexture(640, 360, 0) { name = "RT_SignZoneVideo" };
            _videoImage = UiFactory.Rect("Video", t).gameObject.AddComponent<RawImage>();
            _videoImage.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -170f), new Vector2(880f, 495f));
            _videoImage.texture = _videoTexture;
            _videoImage.raycastTarget = false;
            _videoRect = _videoImage.rectTransform;

            // Live camera for SIGN IT questions (same slot as the video; the video shrinks to an inset).
            _cameraImage = UiFactory.Rect("Camera", t).gameObject.AddComponent<RawImage>();
            _cameraImage.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -170f), new Vector2(880f, 495f));
            _cameraImage.raycastTarget = false;
            _cameraImage.color = Color.white;
            _cameraImage.gameObject.SetActive(false);
            _videoImage.transform.SetAsLastSibling(); // inset video draws on top of the camera

            _video = _videoImage.gameObject.AddComponent<VideoPlayer>();
            _video.playOnAwake = false;
            _video.isLooping = true;
            _video.source = VideoSource.Url;
            _video.renderMode = VideoRenderMode.RenderTexture;
            _video.targetTexture = _videoTexture;
            _video.audioOutputMode = VideoAudioOutputMode.None;

            _prompt = UiFactory.Text("Prompt", t, "", 60f);
            _prompt.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -690f), new Vector2(940f, 100f));
            _prompt.enableAutoSizing = true; _prompt.fontSizeMin = 30f; _prompt.fontSizeMax = 60f;

            for (int i = 0; i < 4; i++)
            {
                int index = i;
                var b = UiFactory.Button($"Choice{i}", t, "", ChoiceColor, () => OnChoice(index), 46f);
                var rt = (RectTransform)b.transform;
                float x = (i % 2 == 0) ? -235f : 235f;
                float y = -820f - (i / 2) * 170f;
                rt.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(x, y), new Vector2(450f, 150f));
                _choices[i] = b;
                _choiceLabels[i] = b.GetComponentInChildren<TextMeshProUGUI>();
                _choiceLabels[i].enableAutoSizing = true; _choiceLabels[i].fontSizeMin = 24f; _choiceLabels[i].fontSizeMax = 46f;
            }

            // Hold-to-sign button (press = start capture, release = classify).
            var sign = UiFactory.Panel("HoldToSign", t, ZoneColor, raycast: true);
            sign.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -860f), new Vector2(700f, 260f));
            var label = UiFactory.Text("Label", sign.transform, "HOLD TO SIGN", 64f);
            label.rectTransform.Stretch();
            _signLabel = label;
            var trigger = sign.gameObject.AddComponent<EventTrigger>();
            AddTrigger(trigger, EventTriggerType.PointerDown, StartCapture);
            AddTrigger(trigger, EventTriggerType.PointerUp, EndCapture);
            _signButton = sign.gameObject;

            _feedback = UiFactory.Text("Feedback", t, "", 72f);
            _feedback.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 40f), new Vector2(940f, 110f));
            _feedback.enableAutoSizing = true; _feedback.fontSizeMin = 30f; _feedback.fontSizeMax = 72f;

            _panel.SetActive(false);

            // Start the camera pipeline now (lobby), so it's warmed up and the camera permission
            // prompt appears before the race rather than in the middle of a Sign Zone.
            FindRecognizer();
        }

        public void Dispose()
        {
            UnhookRecognizer();
            if (_videoTexture) _videoTexture.Release();
        }

        // ---------- flow ----------

        public void Begin(NetworkRacePlayer player, int zone)
        {
            if (IsOpen || !player) return;

            var words = Words();
            if (words.Count < 4)
            {
                Debug.LogWarning("[SignZoneQuiz] Not enough words with videos (Resources/video_manifest). Skipping zone.");
                return;
            }

            _player = player;
            _player.BeginAutopilot();
            _questions.Clear();
            for (int i = 0; i < QuestionsPerZone; i++)
                _questions.Add(MakeQuestion(words, signIt: i == (zone % 2 == 0 ? 2 : 1)));

            _index = 0;
            _correct = 0;
            _panel.SetActive(true);
            ShowQuestion();
        }

        /// <summary>Close without a result (race ended, phase changed).</summary>
        public void Cancel()
        {
            if (!IsOpen) return;
            Close();
        }

        public void Tick(float dt)
        {
            if (!IsOpen) return;

            if (_pauseLeft > 0f)
            {
                _pauseLeft -= dt;
                if (_pauseLeft <= 0f) NextQuestion();
                return;
            }

            if (!_awaitingAnswer) return;

            var q = _questions[_index];
            _timeLeft -= dt;
            _timerFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(_timeLeft / (q.Type == QuestionType.SignIt ? SignSeconds : GuessSeconds)), 1f);

            // PC shortcuts.
            if (q.Type == QuestionType.GuessTheSign)
            {
                for (int i = 0; i < 4; i++)
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i) || Input.GetKeyDown(KeyCode.Keypad1 + i)) { OnChoice(i); return; }
            }
            else
            {
                if (Input.GetKeyDown(KeyCode.Return)) StartCapture();
                if (Input.GetKeyUp(KeyCode.Return)) EndCapture();
            }

            if (_capturing) _signLabel.text = $"SIGNING... {Time.time - _captureStarted:0.0}s";

            if (_timeLeft <= 0f) Answer(false, "TIME'S UP!");
        }

        private void ShowQuestion()
        {
            var q = _questions[_index];
            _header.text = $"SIGN ZONE  {_index + 1}/{QuestionsPerZone}";
            _feedback.text = "";
            _awaitingAnswer = true;
            _capturing = false;

            bool guess = q.Type == QuestionType.GuessTheSign;
            _timeLeft = guess ? GuessSeconds : SignSeconds;

            // Both types show the reference video: guessing needs it, and for SIGN IT it's a
            // model to copy, which is the learning goal.
            PlayVideo(q.Answer);
            ShowCamera(!guess);
            _signLabel.text = "HOLD TO SIGN";

            _prompt.text = guess ? "Which sign is this?" : $"SIGN:  <color=#FFD21A>{q.Answer.ToUpperInvariant()}</color>";

            for (int i = 0; i < 4; i++)
            {
                _choices[i].gameObject.SetActive(guess);
                if (guess)
                {
                    _choiceLabels[i].text = q.Choices[i].ToUpperInvariant();
                    _choices[i].interactable = true;
                    _choices[i].image.color = ChoiceColor;
                }
            }

            _signButton.SetActive(!guess);
            if (!guess) HookRecognizer(q.Answer);
        }

        private void OnChoice(int i)
        {
            if (!_awaitingAnswer) return;
            var q = _questions[_index];
            if (q.Type != QuestionType.GuessTheSign) return;

            bool ok = string.Equals(q.Choices[i], q.Answer, StringComparison.OrdinalIgnoreCase);
            _choices[i].image.color = ok ? Good : Bad;
            if (!ok)
            {
                for (int j = 0; j < 4; j++)
                    if (string.Equals(q.Choices[j], q.Answer, StringComparison.OrdinalIgnoreCase)) _choices[j].image.color = Good;
            }

            Answer(ok, ok ? "CORRECT!" : $"It was {q.Answer.ToUpperInvariant()}");
        }

        private void Answer(bool correct, string message)
        {
            if (!_awaitingAnswer) return;
            _awaitingAnswer = false;
            _capturing = false;
            UnhookRecognizer();

            foreach (var b in _choices) b.interactable = false;

            if (correct) _correct++;
            _feedback.text = message;
            _feedback.color = correct ? Good : Bad;
            if (!correct) LocalPlayerCameraFollow.AddShake(0.3f);

            _pauseLeft = correct ? CorrectPause : WrongPause;
        }

        private void NextQuestion()
        {
            _index++;
            if (_index < _questions.Count) { ShowQuestion(); return; }

            var player = _player;
            int correct = _correct;
            Close();
            if (player) player.ApplySignZoneResult(correct, QuestionsPerZone);
        }

        private void Close()
        {
            UnhookRecognizer();
            _video.Stop();
            _awaitingAnswer = false;
            _pauseLeft = 0f;
            _panel.SetActive(false);
            if (_player) _player.EndAutopilot();
            _player = null;
        }

        // ---------- SIGN IT: recognizer ----------

        private void HookRecognizer(string answer)
        {
            UnhookRecognizer();
            _recognizer = FindRecognizer();
            if (_recognizer == null) return;
            _recognizer.SetVocabulary(SignItVocabulary(answer));
            _recognizer.SignRecognized += OnSignRecognized;
        }

        private void UnhookRecognizer()
        {
            if (_recognizer == null) return;
            if (_capturing) _recognizer.CancelCapture();
            _recognizer.SignRecognized -= OnSignRecognized;
            _recognizer = null;
        }

        private void StartCapture()
        {
            if (!_awaitingAnswer || _recognizer == null || _capturing) return;
            _capturing = true;
            _captureStarted = Time.time;
            _feedback.text = "Sign now, then let go";
            _feedback.color = Color.white;
            _recognizer.BeginCapture();
        }

        private void EndCapture()
        {
            if (!_capturing || _recognizer == null) return;
            _capturing = false;
            _signLabel.text = "HOLD TO SIGN";
            _recognizer.EndCapture(); // result arrives through OnSignRecognized
        }

        private void OnSignRecognized(string word, float confidence)
        {
            if (!_awaitingAnswer) return;
            var answer = _questions[_index].Answer;

            // Nothing recognized (camera warming up, hands out of frame): let them try again.
            if (string.IsNullOrWhiteSpace(word))
            {
                _feedback.text = SlrtkSignRecognizer.IsSupportedPlatform && !SlrtkSignRecognizer.IsReady
                    ? "Camera starting up... try again"
                    : "Didn't catch that. Try again!";
                _feedback.color = Color.white;
                return;
            }

            bool ok = string.Equals((word ?? "").Trim(), answer, StringComparison.OrdinalIgnoreCase);
            Answer(ok, ok ? $"CORRECT!  {Mathf.RoundToInt(confidence * 100f)}%" : $"Not quite. That looked like \"{word}\"");
        }

        /// <summary>
        /// Finds an ISignRecognizer in the scene (the real engine, once it's added). If there is none,
        /// creates the dev recognizer so the demo still works.
        /// </summary>
        private static ISignRecognizer FindRecognizer()
        {
            foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                if (mb is ISignRecognizer r && mb.isActiveAndEnabled) return r;

            // None in the scene: create the SLRTk recognizer (it falls back to dev keys on PC).
            var go = new GameObject("SignRecognizer (SLRTk)");
            return go.AddComponent<SlrtkSignRecognizer>();
        }

        // ---------- content ----------

        private static List<string> Words()
        {
            if (s_words == null) s_words = VideoCatalog.ListAllVideoWords();
            return s_words;
        }

        private static Question MakeQuestion(List<string> words, bool signIt)
        {
            var answer = words[UnityEngine.Random.Range(0, words.Count)];
            var q = new Question { Type = signIt ? QuestionType.SignIt : QuestionType.GuessTheSign, Answer = answer };

            if (!signIt)
            {
                var choices = new List<string> { answer };
                int guard = 0;
                while (choices.Count < 4 && guard++ < 100)
                {
                    var w = words[UnityEngine.Random.Range(0, words.Count)];
                    if (!choices.Contains(w)) choices.Add(w);
                }
                for (int i = choices.Count - 1; i > 0; i--)
                {
                    int j = UnityEngine.Random.Range(0, i + 1);
                    (choices[i], choices[j]) = (choices[j], choices[i]);
                }
                q.Choices = choices.ToArray();
            }
            return q;
        }

        /// <summary>SIGN IT shows your live camera big with the reference video as a small inset.</summary>
        private void ShowCamera(bool signIt)
        {
            var slrtk = _recognizer as SlrtkSignRecognizer ?? FindRecognizer() as SlrtkSignRecognizer;
            bool hasCamera = signIt && SlrtkSignRecognizer.PreviewTexture != null && slrtk != null && !slrtk.UsingFallback;

            _cameraImage.gameObject.SetActive(hasCamera);
            if (hasCamera)
            {
                _cameraImage.texture = SlrtkSignRecognizer.PreviewTexture;
                _cameraImage.uvRect = slrtk.PreviewUvRect;
                // Inset: top-right corner of the camera view.
                _videoRect.Place(new Vector2(0.5f, 1f), new Vector2(1f, 1f), new Vector2(440f, -170f), new Vector2(300f, 169f));
            }
            else
            {
                _videoRect.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -170f), new Vector2(880f, 495f));
            }
        }

        private static List<string> SignItVocabulary(string answer)
        {
            var words = Words();
            var vocab = new List<string> { answer };
            int guard = 0;
            while (vocab.Count < Mathf.Min(SignItVocabularySize, words.Count) && guard++ < 200)
            {
                var w = words[UnityEngine.Random.Range(0, words.Count)];
                if (!vocab.Contains(w)) vocab.Add(w);
            }
            return vocab;
        }

        private void PlayVideo(string word)
        {
            _video.Stop();
            _video.url = VideoCatalog.GetVideoUrlForWord(word);
            _video.Play();
        }

        private static void AddTrigger(EventTrigger trigger, EventTriggerType type, Action action)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(_ => action());
            trigger.triggers.Add(entry);
        }
    }
}
