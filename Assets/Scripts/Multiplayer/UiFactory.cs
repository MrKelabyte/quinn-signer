using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Multiplayer
{
    /// <summary>
    /// Small helpers for building uGUI elements from code. The race HUD, quiz and results
    /// screens are built this way so they need no prefab or scene wiring.
    /// </summary>
    public static class UiFactory
    {
        private static Sprite s_circle;
        private static TMP_FontAsset s_font;

        // Text created from code would otherwise use the project's default TMP font, which renders
        // as solid blocks here. Use the font the scene's own texts already use instead.
        private const string FontResourcePath = "Fonts & Materials/LiberationSans SDF - Fallback";

        /// <summary>Font for all code-built text (UI and 3D labels).</summary>
        public static TMP_FontAsset Font
        {
            get
            {
                if (!s_font) s_font = Resources.Load<TMP_FontAsset>(FontResourcePath);
                return s_font;
            }
            set => s_font = value;
        }

        /// <summary>Use the font from an existing, working text (e.g. one placed in the scene).</summary>
        public static void UseFontFrom(TMP_Text text)
        {
            if (text && text.font) s_font = text.font;
        }

        public static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>Position a rect by anchor (0..1 of the parent), pivot, offset and size.</summary>
        public static RectTransform Place(this RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 offset, Vector2 size)
        {
            rt.anchorMin = rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = offset;
            rt.sizeDelta = size;
            return rt;
        }

        /// <summary>Fill the parent, with an optional margin.</summary>
        public static RectTransform Stretch(this RectTransform rt, float margin = 0f)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(margin, margin);
            rt.offsetMax = new Vector2(-margin, -margin);
            return rt;
        }

        public static Image Panel(string name, Transform parent, Color color, bool raycast = false)
        {
            var rt = Rect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = raycast;
            return img;
        }

        public static Image Circle(string name, Transform parent, Color color)
        {
            var img = Panel(name, parent, color);
            img.sprite = CircleSprite();
            return img;
        }

        public static TextMeshProUGUI Text(string name, Transform parent, string text, float size,
            TextAlignmentOptions align = TextAlignmentOptions.Center, Color? color = null, bool bold = true)
        {
            var rt = Rect(name, parent);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            if (Font) t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.alignment = align;
            t.color = color ?? Color.white;
            t.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.outlineWidth = 0.12f;                       // dark edge so text reads over bright space
            t.outlineColor = new Color32(0, 0, 0, 220);
            return t;
        }

        public static Button Button(string name, Transform parent, string label, Color background, Action onClick, float fontSize = 48f)
        {
            var img = Panel(name, parent, background, raycast: true);
            var button = img.gameObject.AddComponent<Button>();
            button.targetGraphic = img;
            if (onClick != null) button.onClick.AddListener(() => onClick());

            var text = Text("Label", img.transform, label, fontSize);
            text.rectTransform.Stretch();
            return button;
        }

        /// <summary>A soft white circle sprite generated once (no art assets needed).</summary>
        public static Sprite CircleSprite()
        {
            if (s_circle) return s_circle;

            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[size * size];
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r));
                byte a = (byte)(Mathf.Clamp01(r - d) * 255f);
                px[y * size + x] = new Color32(255, 255, 255, a);
            }
            tex.SetPixels32(px);
            tex.Apply();

            s_circle = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            return s_circle;
        }

        public static string Ordinal(int place)
        {
            switch (place)
            {
                case 1: return "1ST";
                case 2: return "2ND";
                case 3: return "3RD";
                default: return place + "TH";
            }
        }

        public static string FormatTime(float seconds)
        {
            if (seconds < 0f) return "--:--.-";
            int m = (int)(seconds / 60f);
            float s = seconds - m * 60f;
            return $"{m}:{s:00.0}";
        }

        /// <summary>m/s to a fun km/h readout.</summary>
        public static int Kph(float metersPerSecond) => Mathf.RoundToInt(metersPerSecond * 3.6f);
    }
}
