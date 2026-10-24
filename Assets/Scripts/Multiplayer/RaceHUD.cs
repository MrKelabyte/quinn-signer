using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer
{
    /// <summary>
    /// In-race HUD: countdown, place, live standings, speed, banners, the virtual joystick,
    /// finale prompts and the end-of-race stats screen.
    ///
    /// Everything is built in code under the existing RaceHUDCanvas, so the scene needs no
    /// extra wiring. The old arrow-race texts on the canvas are hidden.
    /// </summary>
    public class RaceHUD : MonoBehaviour
    {
        // Kept so the existing scene references stay valid. Hidden at runtime.
        public TMP_Text promptText;
        public TMP_Text statusText;
        public TMP_Text countdownText;

        private static readonly Color LocalRowColor = new Color(0.2f, 0.9f, 1f, 1f);
        private static readonly Color BarBack = new Color(0f, 0f, 0f, 0.45f);
        private static readonly Color BoostColor = new Color(1f, 0.8f, 0.1f, 1f);
        private static readonly Color PanelColor = new Color(0.03f, 0.03f, 0.1f, 0.88f);

        private class StandingRow
        {
            public RectTransform Root;
            public TextMeshProUGUI Name;
            public Image Fill;
        }

        private class ResultRow
        {
            public TextMeshProUGUI Place, Name, Time, Signs, Speed, Lasers;
            public Image Highlight;
        }

        private RectTransform _root;
        private TextMeshProUGUI _place, _placeOf, _time, _speed, _boost, _big, _sub;
        private readonly List<StandingRow> _rows = new List<StandingRow>();
        private Image _stickRing, _stickKnob;
        private GameObject _resultsPanel;
        private GameObject _winnerCamPanel;
        private RawImage _winnerCam;
        private TextMeshProUGUI _winnerCamLabel;
        private readonly List<ResultRow> _resultRows = new List<ResultRow>();
        private TextMeshProUGUI _resultsTitle;

        private string _bannerText;
        private Color _bannerColor;
        private float _bannerUntil;
        private NetworkRacePlayer _boundLocal;
        private RacePhase _lastPhase = (RacePhase)255;

        private SignZoneQuiz _quiz;

        private void Awake()
        {
            ConfigureScaler();
            UiFactory.UseFontFrom(countdownText ? countdownText : statusText);

            // Hide the old arrow-race UI on this canvas.
            foreach (Transform child in transform)
                child.gameObject.SetActive(false);

            Build();
            _quiz = new SignZoneQuiz(_root);
        }

        private void OnDestroy()
        {
            Unbind();
            _quiz?.Dispose();
        }

        private void ConfigureScaler()
        {
            var scaler = GetComponent<CanvasScaler>();
            if (!scaler) return;
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 0.5f;
        }

        // ---------- build ----------

        private void Build()
        {
            _root = UiFactory.Rect("HUD (generated)", transform).Stretch();

            // Place (top-left)
            _place = UiFactory.Text("Place", _root, "1ST", 120f, TextAlignmentOptions.TopLeft);
            _place.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -60f), new Vector2(400f, 140f));
            _placeOf = UiFactory.Text("PlaceOf", _root, "of 2", 44f, TextAlignmentOptions.TopLeft, new Color(1f, 1f, 1f, 0.8f), false);
            _placeOf.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(44f, -190f), new Vector2(300f, 60f));

            // Time (top-right)
            _time = UiFactory.Text("Time", _root, "0:00.0", 56f, TextAlignmentOptions.TopRight);
            _time.rectTransform.Place(new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-40f, -70f), new Vector2(400f, 80f));

            // Standings (left, under place)
            for (int i = 0; i < 4; i++)
            {
                var row = new StandingRow { Root = UiFactory.Rect($"Standing{i}", _root) };
                row.Root.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -270f - i * 70f), new Vector2(420f, 60f));
                row.Name = UiFactory.Text("Name", row.Root, "", 34f, TextAlignmentOptions.Left);
                row.Name.rectTransform.Place(new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(420f, 36f));
                var back = UiFactory.Panel("BarBack", row.Root, BarBack);
                back.rectTransform.Place(new Vector2(0f, 0f), new Vector2(0f, 0f), Vector2.zero, new Vector2(420f, 14f));
                row.Fill = UiFactory.Panel("BarFill", back.transform, Color.white);
                var fr = row.Fill.rectTransform;
                fr.anchorMin = Vector2.zero; fr.anchorMax = new Vector2(0f, 1f);
                fr.offsetMin = fr.offsetMax = Vector2.zero; fr.pivot = new Vector2(0f, 0.5f);
                _rows.Add(row);
            }

            // Speed + boost (bottom-right)
            _speed = UiFactory.Text("Speed", _root, "0 KM/H", 64f, TextAlignmentOptions.BottomRight);
            _speed.rectTransform.Place(new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-40f, 60f), new Vector2(500f, 90f));
            _boost = UiFactory.Text("Boost", _root, "", 52f, TextAlignmentOptions.BottomRight, BoostColor);
            _boost.rectTransform.Place(new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-40f, 150f), new Vector2(600f, 80f));

            // Center banner
            _big = UiFactory.Text("Big", _root, "", 150f);
            _big.rectTransform.Place(new Vector2(0.5f, 0.62f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1060f, 220f));
            _big.enableAutoSizing = true; _big.fontSizeMin = 60f; _big.fontSizeMax = 150f;
            _sub = UiFactory.Text("Sub", _root, "", 52f);
            _sub.rectTransform.Place(new Vector2(0.5f, 0.62f), new Vector2(0.5f, 1f), new Vector2(0f, -120f), new Vector2(1000f, 160f));
            _sub.enableAutoSizing = true; _sub.fontSizeMin = 30f; _sub.fontSizeMax = 52f;

            // Virtual joystick (drawn where the thumb goes down)
            _stickRing = UiFactory.Circle("StickRing", _root, new Color(1f, 1f, 1f, 0.15f));
            _stickKnob = UiFactory.Circle("StickKnob", _root, new Color(1f, 1f, 1f, 0.45f));
            _stickRing.rectTransform.anchorMin = _stickRing.rectTransform.anchorMax = Vector2.zero;
            _stickKnob.rectTransform.anchorMin = _stickKnob.rectTransform.anchorMax = Vector2.zero;

            BuildWinnerCam();
            BuildResults();
        }

        /// <summary>"See who beat you": the winner's live camera, top-center during the finale.</summary>
        private void BuildWinnerCam()
        {
            var frame = UiFactory.Panel("WinnerCam", _root, BoostColor);
            frame.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -240f), new Vector2(430f, 286f));
            _winnerCamPanel = frame.gameObject;

            _winnerCam = UiFactory.Rect("Camera", frame.transform).gameObject.AddComponent<RawImage>();
            _winnerCam.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -7f), new Vector2(416f, 234f));
            _winnerCam.raycastTarget = false;

            _winnerCamLabel = UiFactory.Text("Label", frame.transform, "WINNER", 34f, TextAlignmentOptions.Center, Color.black, true);
            _winnerCamLabel.outlineWidth = 0f;
            _winnerCamLabel.rectTransform.Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 2f), new Vector2(420f, 40f));

            _winnerCamPanel.SetActive(false);
        }

        private void UpdateWinnerCam(RaceManager rm, NetworkRacePlayer local, RacePhase phase)
        {
            var tex = phase == RacePhase.Finale ? rm.Finale.WinnerCamTexture : null;
            bool show = tex != null;
            if (_winnerCamPanel.activeSelf != show) _winnerCamPanel.SetActive(show);
            if (!show) return;

            _winnerCam.texture = tex;
            _winnerCam.uvRect = rm.Finale.WinnerCamUvRect;
            bool youWon = local && rm.IsWinner(local.OwnerClientId);
            _winnerCamLabel.text = youWon ? "SMILE! EVERYONE SEES YOU" : $"WINNER: {WinnerName(rm)}";
        }

        private void BuildResults()
        {
            var panel = UiFactory.Panel("Results", _root, PanelColor, raycast: true);
            panel.rectTransform.Stretch();
            _resultsPanel = panel.gameObject;

            _resultsTitle = UiFactory.Text("Title", panel.transform, "RESULTS", 110f);
            _resultsTitle.rectTransform.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -120f), new Vector2(1000f, 150f));

            var header = MakeResultRow(panel.transform, -330f, true);
            header.Place.text = "#"; header.Name.text = "PILOT"; header.Time.text = "TIME";
            header.Signs.text = "SIGNS"; header.Speed.text = "TOP KM/H"; header.Lasers.text = "LASERS";

            for (int i = 0; i < 4; i++)
                _resultRows.Add(MakeResultRow(panel.transform, -430f - i * 110f, false));

            var close = UiFactory.Button("Close", panel.transform, "BACK TO LOBBY", new Color(0.15f, 0.7f, 1f, 1f), RaceCanvasController.DismissResults, 56f);
            ((RectTransform)close.transform).Place(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 160f), new Vector2(700f, 150f));

            _resultsPanel.SetActive(false);
        }

        private ResultRow MakeResultRow(Transform parent, float y, bool header)
        {
            var root = UiFactory.Rect(header ? "Header" : "Row", parent);
            root.Place(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, y), new Vector2(1020f, 96f));

            var row = new ResultRow();
            row.Highlight = UiFactory.Panel("Highlight", root, new Color(0.2f, 0.9f, 1f, 0.18f));
            row.Highlight.rectTransform.Stretch();
            row.Highlight.enabled = false;

            float size = header ? 30f : 40f;
            var c = header ? new Color(1f, 1f, 1f, 0.6f) : Color.white;
            row.Place  = Column(root, "Place",  0f,   80f, size, c);
            row.Name   = Column(root, "Name",   80f,  330f, size, c, TextAlignmentOptions.Left);
            row.Time   = Column(root, "Time",   410f, 170f, size, c);
            row.Signs  = Column(root, "Signs",  580f, 140f, size, c);
            row.Speed  = Column(root, "Speed",  720f, 170f, size, c);
            row.Lasers = Column(root, "Lasers", 890f, 130f, size, c);
            return row;
        }

        private static TextMeshProUGUI Column(Transform parent, string name, float x, float width, float size, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.Center)
        {
            var t = UiFactory.Text(name, parent, "", size, align, color);
            t.rectTransform.Place(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(x, 0f), new Vector2(width, 90f));
            t.enableAutoSizing = true; t.fontSizeMin = 20f; t.fontSizeMax = size;
            return t;
        }

        // ---------- local player events ----------

        private void Bind(NetworkRacePlayer p)
        {
            Unbind();
            _boundLocal = p;
            if (!p) return;
            p.WallBumped += OnWallBumped;
            p.BoostStarted += OnBoostStarted;
            p.SignZoneEntered += OnSignZoneEntered;
        }

        private void Unbind()
        {
            if (!_boundLocal) { _boundLocal = null; return; }
            _boundLocal.WallBumped -= OnWallBumped;
            _boundLocal.BoostStarted -= OnBoostStarted;
            _boundLocal.SignZoneEntered -= OnSignZoneEntered;
            _boundLocal = null;
        }

        private void OnWallBumped() => LocalPlayerCameraFollow.AddShake(0.35f);

        private void OnBoostStarted(float multiplier)
        {
            ShowBanner($"BOOST x{multiplier:0.##}!", BoostColor, 1.5f);
            LocalPlayerCameraFollow.AddShake(0.25f);
        }

        private void OnSignZoneEntered(int zone) => _quiz.Begin(_boundLocal, zone);

        public void ShowBanner(string text, Color color, float seconds)
        {
            _bannerText = text;
            _bannerColor = color;
            _bannerUntil = Time.time + seconds;
        }

        // ---------- per-frame ----------

        private void Update()
        {
            var rm = RaceManager.Instance;
            var local = NetworkRacePlayer.Local;
            if (local != _boundLocal) Bind(local);
            if (rm == null || !rm.IsSpawned) return;

            var phase = rm.Phase.Value;
            if (phase != _lastPhase) OnPhaseChanged(_lastPhase, phase);

            _quiz.Tick(Time.deltaTime);

            UpdateStandings(rm, local);
            UpdateSpeed(local, phase);
            UpdateCenter(rm, local, phase);
            UpdateJoystick(local, phase);
            UpdateWinnerCam(rm, local, phase);
            UpdateResults(rm, local, phase);
        }

        private void OnPhaseChanged(RacePhase from, RacePhase to)
        {
            _lastPhase = to;
            if (to == RacePhase.Racing) ShowBanner("GO!", new Color(0.3f, 1f, 0.4f), 1f);
            if (to != RacePhase.Racing) _quiz.Cancel();
        }

        private void UpdateStandings(RaceManager rm, NetworkRacePlayer local)
        {
            var standings = rm.GetStandings();
            float length = rm.Course != null ? rm.Course.Length : 1f;

            int localPlace = 1;
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                bool show = i < standings.Count;
                row.Root.gameObject.SetActive(show);
                if (!show) continue;

                var p = standings[i];
                bool isLocal = p == local;
                if (isLocal) localPlace = i + 1;

                row.Name.text = $"{i + 1}. {p.GetPlayerName()}";
                row.Name.color = isLocal ? LocalRowColor : Color.white;
                row.Fill.color = isLocal ? LocalRowColor : new Color(1f, 1f, 1f, 0.7f);
                float f = Mathf.Clamp01(p.Progress.Value / length);
                row.Fill.rectTransform.anchorMax = new Vector2(f, 1f);
            }

            _place.text = UiFactory.Ordinal(localPlace);
            _placeOf.text = $"of {Mathf.Max(1, standings.Count)}";
            _time.text = UiFactory.FormatTime(local != null && local.HasFinished ? local.FinishTime.Value : rm.RaceTime);
        }

        private void UpdateSpeed(NetworkRacePlayer local, RacePhase phase)
        {
            if (!local) { _speed.text = ""; _boost.text = ""; return; }
            _speed.text = $"{UiFactory.Kph(local.Speed)} KM/H";
            float b = local.BoostMultiplier;
            _boost.text = b > 1.01f ? $"BOOST x{b:0.##}" : (b < 0.99f ? "SLOWED" : "");
            _boost.color = b >= 1f ? BoostColor : new Color(1f, 0.4f, 0.4f);
        }

        private void UpdateCenter(RaceManager rm, NetworkRacePlayer local, RacePhase phase)
        {
            string big = "", sub = "";
            var bigColor = Color.white;

            switch (phase)
            {
                case RacePhase.Countdown:
                    big = Mathf.CeilToInt(rm.CountdownRemaining).ToString();
                    sub = "Drag to steer  (WASD / arrows on PC)";
                    break;

                case RacePhase.Racing:
                    if (rm.WinnerClientId.Value != RaceManager.NoWinner)
                    {
                        bool youWon = local && rm.IsWinner(local.OwnerClientId);
                        big = youWon ? "YOU WIN!" : $"{WinnerName(rm)} WINS!";
                        bigColor = youWon ? BoostColor : Color.white;
                        if (local && local.HasFinished && !youWon) sub = "Finished!";
                    }
                    else if (local && local.HasFinished)
                    {
                        big = "FINISHED!";
                    }
                    break;

                case RacePhase.Finale:
                    bool winner = local && rm.IsWinner(local.OwnerClientId);
                    if (!rm.Finale.CanFire)
                    {
                        big = winner ? "YOU WIN!" : $"{WinnerName(rm)} WINS!";
                        bigColor = BoostColor;
                        sub = winner ? "Get ready to fire..." : "";
                    }
                    else if (rm.Finale.TargetsRemaining > 0)
                    {
                        big = winner ? "TAP TO FIRE!" : $"{WinnerName(rm)} IS FIRING!";
                        bigColor = new Color(1f, 0.3f, 0.3f);
                        sub = $"Ships left: {rm.Finale.TargetsRemaining}";
                    }
                    else
                    {
                        big = rm.Players.Count > 1 ? "BOOM!" : "VICTORY LAP!";
                        bigColor = BoostColor;
                    }
                    break;
            }

            // Short-lived banners (GO!, BOOST!) win if nothing more important is showing.
            if (string.IsNullOrEmpty(big) && Time.time < _bannerUntil)
            {
                big = _bannerText;
                bigColor = _bannerColor;
            }

            // The quiz owns the middle of the screen while it's open.
            if (_quiz.IsOpen) { big = ""; sub = ""; }

            _big.text = big;
            _big.color = bigColor;
            _sub.text = sub;
        }

        private void UpdateJoystick(NetworkRacePlayer local, RacePhase phase)
        {
            var input = local ? local.Controls : null;
            bool show = input != null && input.IsDragging && phase == RacePhase.Racing && !local.IsAutopilot && !local.HasFinished;
            _stickRing.enabled = show;
            _stickKnob.enabled = show;
            if (!show) return;

            float scale = _root.lossyScale.x > 0f ? 1f / _root.lossyScale.x : 1f; // screen px -> canvas units
            float radius = input.StickRadiusPixels * scale;
            var origin = input.DragOrigin * scale;
            var knob = origin + Vector2.ClampMagnitude((input.DragCurrent - input.DragOrigin) * scale, radius);

            _stickRing.rectTransform.anchoredPosition = origin;
            _stickRing.rectTransform.sizeDelta = Vector2.one * radius * 2f;
            _stickKnob.rectTransform.anchoredPosition = knob;
            _stickKnob.rectTransform.sizeDelta = Vector2.one * radius * 0.8f;
        }

        private void UpdateResults(RaceManager rm, NetworkRacePlayer local, RacePhase phase)
        {
            bool show = phase == RacePhase.Results;
            if (_resultsPanel.activeSelf != show) _resultsPanel.SetActive(show);
            if (!show) return;

            var standings = rm.GetStandings();
            int localPlace = 0;
            for (int i = 0; i < _resultRows.Count; i++)
            {
                var row = _resultRows[i];
                bool has = i < standings.Count;
                row.Place.transform.parent.gameObject.SetActive(has);
                if (!has) continue;

                var p = standings[i];
                bool isLocal = p == local;
                if (isLocal) localPlace = i + 1;

                row.Highlight.enabled = isLocal;
                row.Place.text = (i + 1).ToString();
                row.Name.text = p.GetPlayerName();
                row.Time.text = p.HasFinished ? UiFactory.FormatTime(p.FinishTime.Value) : $"DNF {Mathf.RoundToInt(100f * p.Progress.Value / Mathf.Max(1f, rm.Course.Length))}%";
                row.Signs.text = $"{p.SignsCorrect.Value}/{p.SignsAnswered.Value}";
                row.Speed.text = UiFactory.Kph(p.TopSpeed.Value).ToString();
                row.Lasers.text = p.LasersFired.Value.ToString();
            }

            _resultsTitle.text = localPlace == 1 ? "YOU WON!" : localPlace > 0 ? $"YOU PLACED {UiFactory.Ordinal(localPlace)}" : "RESULTS";
        }

        private static string WinnerName(RaceManager rm)
        {
            return NetworkRacePlayer.All.TryGetValue(rm.WinnerClientId.Value, out var p) && p ? p.GetPlayerName() : "SOMEONE";
        }
    }
}
