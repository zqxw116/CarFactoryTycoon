using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>One logical coordinate space for prototype drawing and pointer tests.</summary>
public static class PrototypeGui
{
    public static float ScaleFor(Vector2 screen) => Mathf.Max(.01f, Mathf.Min(screen.x / 960f, screen.y / 540f));
    public static float Scale => ScaleFor(new Vector2(Screen.width, Screen.height));
    public static float Width => Screen.width / Scale;
    public static float Height => Screen.height / Scale;
    public static Vector2 PointerPosition
    {
        get
        {
            if (Mouse.current == null) return new Vector2(-1f, -1f);
            Vector2 pointer = Mouse.current.position.ReadValue();
            return new Vector2(pointer.x, Screen.height - pointer.y) / Scale;
        }
    }
    // Rects use Unity IMGUI's top-left origin, including outside OnGUI.
    public static Rect ToScreenRect(Rect rect) => ToScreenRect(rect, new Vector2(Screen.width, Screen.height));
    public static Rect ToScreenRect(Rect rect, Vector2 screen)
    {
        float scale = ScaleFor(screen);
        return new Rect(rect.position * scale, rect.size * scale);
    }
    public static GuiScope Scope() => new GuiScope(true);
    public readonly struct GuiScope : IDisposable
    {
        readonly Matrix4x4 matrix;
        readonly bool enabled;
        readonly Color color, backgroundColor, contentColor;
        public GuiScope(bool apply = true)
        {
            matrix = GUI.matrix;
            enabled = GUI.enabled;
            color = GUI.color;
            backgroundColor = GUI.backgroundColor;
            contentColor = GUI.contentColor;
            GUI.matrix = matrix * Matrix4x4.Scale(new Vector3(Scale, Scale, 1f));
        }
        public void Dispose()
        {
            GUI.matrix = matrix;
            GUI.enabled = enabled;
            GUI.color = color;
            GUI.backgroundColor = backgroundColor;
            GUI.contentColor = contentColor;
        }
    }
}
