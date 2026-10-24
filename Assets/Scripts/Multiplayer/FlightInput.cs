using UnityEngine;
using UnityEngine.EventSystems;

namespace Multiplayer
{
    /// <summary>
    /// Reads steering for the local ship. One shared reader for keyboard, mouse and touch,
    /// so the ship code never cares which device is used.
    ///
    /// - Keyboard: WASD / arrow keys.
    /// - Phone: put a thumb down anywhere (not on a button) and drag; it works like a virtual joystick
    ///   centered where you touched.
    /// - Mouse: click-drag behaves like a thumb, so phone controls can be tested on PC.
    ///
    /// Plain C# class (not a component) owned by the local NetworkRacePlayer: nothing to wire up.
    /// </summary>
    public class FlightInput
    {
        /// <summary>Steering in -1..1. x = left/right, y = up/down (up = climb).</summary>
        public Vector2 Steer { get; private set; }

        /// <summary>True while a thumb (or mouse) is held down for steering.</summary>
        public bool IsDragging { get; private set; }

        /// <summary>Screen position where the current drag started (the joystick center).</summary>
        public Vector2 DragOrigin { get; private set; }

        /// <summary>Current screen position of the dragging thumb.</summary>
        public Vector2 DragCurrent { get; private set; }

        /// <summary>Number of new taps/clicks this frame that did not land on UI.</summary>
        public int TapsThisFrame { get; private set; }

        /// <summary>Joystick radius as a fraction of screen height.</summary>
        public float StickRadiusFraction { get; set; } = 0.12f;

        public float StickRadiusPixels => Screen.height * StickRadiusFraction;

        private int _fingerId = -1;
        private bool _mouseDragging;

        public void Update()
        {
            TapsThisFrame = 0;

            var steer = ReadKeyboard();

            if (Input.touchCount > 0)
                steer += ReadTouches();
            else
                steer += ReadMouse();

            Steer = Vector2.ClampMagnitude(steer, 1f);
        }

        private static Vector2 ReadKeyboard()
        {
            float h = 0f, v = 0f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) h -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) h += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) v -= 1f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) v += 1f;
            return new Vector2(h, v);
        }

        private Vector2 ReadTouches()
        {
            _mouseDragging = false;

            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                if (t.phase != TouchPhase.Began) continue;
                if (IsOverUi(t.fingerId)) continue;

                TapsThisFrame++;
                if (_fingerId == -1)
                {
                    _fingerId = t.fingerId;
                    DragOrigin = t.position;
                }
            }

            if (_fingerId == -1) { IsDragging = false; return Vector2.zero; }

            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                if (t.fingerId != _fingerId) continue; // keep looking: other fingers must not cancel steering

                if (t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled)
                {
                    _fingerId = -1;
                    IsDragging = false;
                    return Vector2.zero;
                }

                IsDragging = true;
                DragCurrent = t.position;
                return (DragCurrent - DragOrigin) / StickRadiusPixels;
            }

            // Our finger vanished without an Ended phase.
            _fingerId = -1;
            IsDragging = false;
            return Vector2.zero;
        }

        private Vector2 ReadMouse()
        {
            _fingerId = -1;

            if (Input.GetMouseButtonDown(0) && !IsOverUi(-1))
            {
                TapsThisFrame++;
                _mouseDragging = true;
                DragOrigin = Input.mousePosition;
            }

            if (_mouseDragging && !Input.GetMouseButton(0))
                _mouseDragging = false;

            IsDragging = _mouseDragging;
            if (!_mouseDragging) return Vector2.zero;

            DragCurrent = Input.mousePosition;
            return (DragCurrent - DragOrigin) / StickRadiusPixels;
        }

        private static bool IsOverUi(int pointerId)
        {
            var es = EventSystem.current;
            if (es == null) return false;
            return pointerId < 0 ? es.IsPointerOverGameObject() : es.IsPointerOverGameObject(pointerId);
        }
    }
}
