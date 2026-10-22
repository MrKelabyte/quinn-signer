using Features.Signing;
using Features.Signing.Recognition;
using UnityEngine;
using UnityEngine.UI;

namespace Core.SceneManagement
{
    public class SceneBindings : MonoBehaviour
    {
        public WordBank wordBank;

        [Tooltip("Optional. A component implementing ISignRecognizer to use in this scene. " +
                 "Leave empty to keep the Signer's own recognizer.")]
        public MonoBehaviour recognizer;

        public Text inferenceText;
        public Text condifenceScoreText;
        public Image background;

        public ISignRecognizer Recognizer => recognizer as ISignRecognizer;
    }
}
