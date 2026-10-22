using System;
using System.Collections.Generic;
using Features.Gameplay.Entities.Enemy;
using UnityEngine;

namespace Features.Signing.Recognition
{
    /// <summary>
    /// Stand-in sign recognizer for testing without a camera engine.
    ///
    /// - Release the sign button (Return key or the mobile hold button) to report a CORRECT sign.
    /// - Hold Shift while releasing to report a WRONG sign instead.
    /// - Press 1 / 2 at any time to instantly report a correct / wrong sign.
    ///
    /// Only active in the Editor and in Development Builds. In a release build it never
    /// reports anything, so players can't trigger it.
    /// </summary>
    public class DevKeySignRecognizer : MonoBehaviour, ISignRecognizer
    {
        public const string WrongWord = "__dev_wrong__";
        private const string NoTargetWord = "dev_correct_missing";

        // Only read in Editor/Development builds, so release builds would warn "never used".
#pragma warning disable CS0414
        [Tooltip("Press to instantly report a correct sign.")]
        [SerializeField] private KeyCode instantCorrectKey = KeyCode.Alpha1;

        [Tooltip("Press to instantly report a wrong sign.")]
        [SerializeField] private KeyCode instantWrongKey = KeyCode.Alpha2;
#pragma warning restore CS0414

        [Tooltip("Confidence reported with a correct sign (shown as a % in the HUD).")]
        [SerializeField, Range(0f, 1f)] private float correctConfidence = 1f;

        public event Action<string, float> SignRecognized;
        public bool IsCapturing { get; private set; }

        private readonly List<string> _vocabulary = new List<string>();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private void Update()
        {
            if (Input.GetKeyDown(instantCorrectKey)) ReportCorrect();
            if (Input.GetKeyDown(instantWrongKey)) ReportWrong();
        }
#endif

        public void BeginCapture() => IsCapturing = true;

        public void EndCapture()
        {
            if (!IsCapturing) return;
            IsCapturing = false;

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            var wrong = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (wrong) ReportWrong();
            else ReportCorrect();
#else
            Debug.LogWarning("[DevKeySignRecognizer] Does nothing in release builds. Assign a real ISignRecognizer.", this);
#endif
        }

        public void CancelCapture() => IsCapturing = false;

        public void SetVocabulary(IReadOnlyList<string> words)
        {
            _vocabulary.Clear();
            if (words != null) _vocabulary.AddRange(words);
        }

        private void ReportCorrect() => SignRecognized?.Invoke(PickCorrectWord(), correctConfidence);
        private void ReportWrong() => SignRecognized?.Invoke(WrongWord, 0f);

        /// <summary>
        /// "Correct" means what a player would sign: the word on an enemy that is on screen.
        /// Falls back to the vocabulary, then to a placeholder that matches nothing.
        /// Scene search is fine here: it only runs on a dev key press, never per frame.
        /// </summary>
        private string PickCorrectWord()
        {
            foreach (var label in FindObjectsByType<EnemyLabel>(FindObjectsSortMode.None))
            {
                if (label && !string.IsNullOrEmpty(label.targetWord))
                    return label.targetWord;
            }

            return _vocabulary.Count > 0 ? _vocabulary[0] : NoTargetWord;
        }
    }
}
