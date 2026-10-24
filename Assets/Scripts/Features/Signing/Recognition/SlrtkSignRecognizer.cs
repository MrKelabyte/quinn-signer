using System;
using System.Collections.Generic;
using System.Linq;
using GTk.Bindings;
using UnityEngine;
using UnityEngine.UI;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace Features.Signing.Recognition
{
    /// <summary>
    /// ISignRecognizer backed by the SLRTk camera pipeline (Assets/SLRTk).
    ///
    /// How it works (same pattern as SLRTk's InteractiveSLRSample, which this adapts):
    /// - The pipeline is opened ONCE for the whole app, after the native GPU is ready, and is
    ///   never closed until the app quits. SLRTk does not survive being re-opened, and opening
    ///   costs seconds of camera warm-up.
    /// - The camera records continuously into a rolling buffer. BeginCapture clears the buffer
    ///   (start of the sign), EndCapture asks the model what was signed since then.
    /// - SetVocabulary limits the answer to the words currently in play.
    ///
    /// SLRTk only ships native plugins for Android, iOS and macOS. Everywhere else (e.g. the
    /// Windows Editor) this component automatically falls back to DevKeySignRecognizer, so the
    /// game stays testable on PC and the same component can be used in every scene.
    /// </summary>
    public class SlrtkSignRecognizer : MonoBehaviour, ISignRecognizer
    {
        [Header("Recognition")]
        [Tooltip("Which SLRTk gallery backs recognition. Must be set (see SLRTk's InteractiveSLRSample).")]
        [SerializeField] private string gallery = "popsign";
        [SerializeField] private int topK = 5;

        [Header("Preview (optional)")]
        [Tooltip("If set, shows the live camera here. Code can also read PreviewTexture/PreviewUvRect.")]
        [SerializeField] private RawImage previewImage;
        [Tooltip("Mirror the preview like a selfie camera. Display only; recognition is unaffected.")]
        [SerializeField] private bool mirrorPreview = true;
        [Tooltip("Vertical flip for the preview. SLRTk's 'Auto' showed the camera upside down on our test phone " +
                 "(Android, OpenGLES3), so the default is NotFlipped. Display only.")]
        [SerializeField] private SLRTk.SlrtkPreviewOrientation.FlipOverride previewFlip = SLRTk.SlrtkPreviewOrientation.FlipOverride.NotFlipped;
        [SerializeField] private bool drawLandmarkOverlay = true;

        public event Action<string, float> SignRecognized;
        public bool IsCapturing { get; private set; }

        /// <summary>True when the real camera pipeline is open and recording.</summary>
        public static bool IsReady => s_pipeline != null && s_recording;

        /// <summary>True on platforms SLRTk has native plugins for.</summary>
        public static bool IsSupportedPlatform
        {
            get
            {
                switch (Application.platform)
                {
                    case RuntimePlatform.Android:
                    case RuntimePlatform.IPhonePlayer:
                    case RuntimePlatform.OSXPlayer:
                    case RuntimePlatform.OSXEditor:
                        return true;
                    default:
                        return false;
                }
            }
        }

        /// <summary>Live camera image (null until the pipeline is open).</summary>
        public static Texture PreviewTexture => s_texture;

        /// <summary>UV rect to draw PreviewTexture upright (and mirrored if requested).</summary>
        public Rect PreviewUvRect
        {
            get
            {
                var uv = PreviewFlipRect(previewFlip);
                return mirrorPreview ? new Rect(uv.x + uv.width, uv.y, -uv.width, uv.height) : uv;
            }
        }

        private static Rect PreviewFlipRect(SLRTk.SlrtkPreviewOrientation.FlipOverride flip)
        {
            // Same rule as SlrtkPreviewOrientation.UvRect, without its per-call Debug.Log
            // (this is read every frame).
            bool flipped = flip == SLRTk.SlrtkPreviewOrientation.FlipOverride.Auto
                ? !SystemInfo.graphicsUVStartsAtTop
                : flip == SLRTk.SlrtkPreviewOrientation.FlipOverride.Flipped;
            return flipped ? new Rect(0, 1, 1, -1) : new Rect(0, 0, 1, 1);
        }

        /// <summary>True when this instance is using the dev-key fallback instead of the camera.</summary>
        public bool UsingFallback => _fallback != null;

        // ---- one pipeline for the whole app ----
        private static DynamicSLRPipelineService s_pipeline;
        private static RenderTexture s_texture;
        private static bool s_recording;
        private static bool s_opening;
        private static bool s_quitHooked;
        private static List<string> s_vocabulary = new List<string>();
        private static int s_activeUsers;

        private DevKeySignRecognizer _fallback;
        private bool _waitingForGpu;
        private bool _counted;

        // ---------- lifecycle ----------

        private void Awake()
        {
            if (!IsSupportedPlatform)
            {
                Debug.Log($"[SlrtkSignRecognizer] SLRTk has no native plugin for {Application.platform}; using dev keys instead.");
                _fallback = gameObject.GetComponent<DevKeySignRecognizer>();
                if (!_fallback) _fallback = gameObject.AddComponent<DevKeySignRecognizer>();
                _fallback.SignRecognized += ForwardFallback;
                return;
            }

            RequestCameraPermission();
        }

        private void OnEnable()
        {
            if (_fallback) return;
            s_activeUsers++;
            _counted = true;
            if (s_pipeline != null) ResumeRecording();
        }

        private void OnDisable()
        {
            if (!_counted) return;
            _counted = false;
            s_activeUsers = Mathf.Max(0, s_activeUsers - 1);
            CancelCapture();
            if (s_activeUsers == 0) SuspendRecording();
        }

        private void OnDestroy()
        {
            if (_fallback) _fallback.SignRecognized -= ForwardFallback;
            if (_waitingForGpu) NativePipelineHooks.OnGpuInitialized -= OnGpuReady;
        }

        private void OnApplicationPause(bool paused)
        {
            if (_fallback) return;
            if (paused) SuspendRecording();
            else if (s_activeUsers > 0) ResumeRecording();
        }

        private void Update()
        {
            if (_fallback) return;

            // Open the pipeline once the camera permission is granted (Android asks at runtime).
            if (s_pipeline == null && !s_opening && !_waitingForGpu && HasCameraPermission())
            {
                _waitingForGpu = true;
                NativePipelineHooks.OnGpuInitialized += OnGpuReady; // latched: fires now if the GPU is already up
            }

            ActivePreviewUvRect = PreviewUvRect;

            if (previewImage && s_texture)
            {
                previewImage.texture = s_texture;
                previewImage.uvRect = ActivePreviewUvRect;
            }
        }

        /// <summary>UV rect of the active recognizer's preview (orientation + mirror), for code that
        /// draws PreviewTexture or frames copied from it.</summary>
        public static Rect ActivePreviewUvRect { get; private set; } = new Rect(1f, 0f, -1f, 1f);

        // ---------- ISignRecognizer ----------

        public void BeginCapture()
        {
            if (_fallback) { _fallback.BeginCapture(); return; }

            IsCapturing = true;
            // The sign starts now: drop anything recorded before.
            if (IsReady) s_pipeline.ClearBuffer(new DynamicSLREmpty());
        }

        public void EndCapture()
        {
            if (_fallback) { _fallback.EndCapture(); return; }
            if (!IsCapturing) return;
            IsCapturing = false;

            if (!IsReady)
            {
                Debug.LogWarning("[SlrtkSignRecognizer] Camera is still starting up; nothing recognized.");
                SignRecognized?.Invoke("", 0f);
                return;
            }

            string label = "";
            float score = 0f;
            try
            {
                var response = s_pipeline.TriggerRecognition(new DynamicSLREmpty());
                if (response == null || !response.Success)
                {
                    Debug.LogError("[SlrtkSignRecognizer] Recognition failed: " + (response?.ErrorMessage ?? "null response"));
                }
                else if (response.Classifications.Count > 0)
                {
                    var best = response.Classifications.OrderByDescending(c => c.Score).First();
                    label = best.Label;
                    score = best.Score;
                }
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                // Never let this clip's frames leak into the next sign.
                if (s_pipeline != null) s_pipeline.ClearBuffer(new DynamicSLREmpty());
            }

            SignRecognized?.Invoke(label, Mathf.Clamp01(score));
        }

        public void CancelCapture()
        {
            if (_fallback) { _fallback.CancelCapture(); return; }
            IsCapturing = false;
            if (IsReady) s_pipeline.ClearBuffer(new DynamicSLREmpty());
        }

        public void SetVocabulary(IReadOnlyList<string> words)
        {
            if (_fallback) { _fallback.SetVocabulary(words); return; }

            s_vocabulary = words != null
                ? words.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim().ToLowerInvariant()).Distinct().ToList()
                : new List<string>();
            ApplyVocabulary();
        }

        private void ForwardFallback(string word, float confidence) => SignRecognized?.Invoke(word, confidence);

        // ---------- pipeline ----------

        private void OnGpuReady()
        {
            NativePipelineHooks.OnGpuInitialized -= OnGpuReady;
            _waitingForGpu = false;
            if (s_pipeline != null || s_opening) return;

            s_opening = true;
            try
            {
                HookQuit();

                s_texture = new RenderTexture(1280, 720, 0, RenderTextureFormat.BGRA32)
                {
                    enableRandomWrite = true,
                    hideFlags = HideFlags.DontUnloadUnusedAsset,
                    name = "SLRTk Preview"
                };
                s_texture.Create();

                var pipeline = new DynamicSLRPipelineService();

                // Gallery MUST be set: without it the options serialize empty and SLRTk silently
                // builds its legacy recognizer (see InteractiveSLRSample.SetupPipeline).
                var options = new CoreTk.Pipeline.PipelineBaseOptions
                {
                    EnableHdf5 = false,
                    EnableOverlay = drawLandmarkOverlay,
                    Overlay = new CoreTk.Pipeline.OverlayOptions { DrawPose = false },
                    SignRecognition = new CoreTk.Pipeline.SignRecognitionOptions
                    {
                        Modality = CoreTk.Pipeline.SignRecognitionOptions.Types.Modality.Isolated,
                        Language = CoreTk.Pipeline.SignRecognitionOptions.Types.Language.Asl,
                        Gallery = gallery,
                        TopK = topK,
                    },
                };

                var open = pipeline.OpenPipeline(new DynamicSLROpenRequest { Options = options });
                if (open != null && !open.Success)
                {
                    Debug.LogError("[SlrtkSignRecognizer] OpenPipeline failed: " + open.ErrorMessage);
                    pipeline.Dispose();
                    return;
                }

                pipeline.AddDynamicSLR(new AddDynamicSLRRequest
                {
                    TargetGraph = "GTk/Pipelines/DynamicSLR",
                    TexturePtr = (ulong)s_texture.GetNativeTexturePtr().ToInt64(),
                    ViewPtr = 0,
                    // GL needs explicit size (see InteractiveSLRSample).
                    TextureWidth = (uint)s_texture.width,
                    TextureHeight = (uint)s_texture.height
                });

                s_pipeline = pipeline;
                if (s_activeUsers > 0) ResumeRecording();
                ApplyVocabulary();
                Debug.Log("[SlrtkSignRecognizer] SLRTk pipeline open.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SlrtkSignRecognizer] Failed to start SLRTk: {e.Message}\n{e.StackTrace}");
            }
            finally
            {
                s_opening = false;
            }
        }

        private static void ResumeRecording()
        {
            if (s_pipeline == null || s_recording) return;
            // Empty string, not null (null throws inside the protobuf setter).
            s_pipeline.StartRecording(new DynamicSLRStartRequest { OutputHdf5File = "" });
            s_pipeline.ClearBuffer(new DynamicSLREmpty());
            s_recording = true;
        }

        private static void SuspendRecording()
        {
            if (s_pipeline == null || !s_recording) return;
            s_pipeline.StopRecording(new DynamicSLREmpty());
            s_pipeline.ClearBuffer(new DynamicSLREmpty());
            s_recording = false;
        }

        private static void ApplyVocabulary()
        {
            if (s_pipeline == null || s_vocabulary.Count == 0) return;
            var request = new DynamicSLRSetVocabRequest();
            foreach (var w in s_vocabulary) request.Vocab.Add(w);
            s_pipeline.SetVocabulary(request);
        }

        private static void HookQuit()
        {
            if (s_quitHooked) return;
            Application.quitting += CloseForGood;
            s_quitHooked = true;
        }

        private static void CloseForGood()
        {
            SuspendRecording();
            if (s_pipeline != null) { s_pipeline.Dispose(); s_pipeline = null; }
            if (s_texture != null) { s_texture.Release(); s_texture = null; }
        }

        // ---------- camera permission ----------

        private static void RequestCameraPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
                Permission.RequestUserPermission(Permission.Camera);
#endif
        }

        private static bool HasCameraPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return Permission.HasUserAuthorizedPermission(Permission.Camera);
#else
            return true;
#endif
        }
    }
}
