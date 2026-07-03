using UnityEngine;

namespace FsmMaster;

// Runtime-generated style resources + palette for FsmMaster's right panel - instance-owned (built in
// FsmMasterPlugin.Awake, torn down via Destroy() in OnDestroy) rather than static, so a ScriptEngine
// reload can never accumulate orphaned Texture2D/Sprite instances across reloads (see CLAUDE.md's
// Awake/OnDestroy symmetry contract). Every widget constructor takes a UICommon reference rather than
// reading shared static fields, for the same reason. Palette is FsmMaster's own dark/translucent
// theme - a distinct set of literal color values from Silksong.DebugMod's Catppuccin Macchiato
// palette (agent-context/Silksong.DebugMod-main/UI/UICommon.cs), not copied from it.
internal sealed class UICommon
{
    public Texture2D SolidTexture { get; }
    public Sprite SolidSprite { get; }
    public Font? BodyFont { get; }

    public Color PanelBackground { get; } = new(28f / 255f, 30f / 255f, 36f / 255f, 0.92f);
    public Color PanelBorder { get; } = new(70f / 255f, 74f / 255f, 84f / 255f, 1f);
    public Color ButtonNormal { get; } = new(42f / 255f, 45f / 255f, 53f / 255f, 0.95f);
    public Color ButtonActive { get; } = new(58f / 255f, 98f / 255f, 150f / 255f, 0.95f);
    public Color AccentColor { get; } = new(120f / 255f, 170f / 255f, 235f / 255f, 1f);
    public Color TextColor { get; } = new(230f / 255f, 230f / 255f, 235f / 255f, 1f);
    public Color ScrollTrackColor { get; } = new(50f / 255f, 53f / 255f, 61f / 255f, 0.6f);

    public int FontSize => ScaleHeight(13);

    public UICommon()
    {
        SolidTexture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        SolidTexture.SetPixel(0, 0, Color.white);
        SolidTexture.Apply();

        SolidSprite = Sprite.Create(SolidTexture, new Rect(0f, 0f, 1f, 1f), Vector2.zero);
        SolidSprite.hideFlags = HideFlags.HideAndDontSave;

        // Unity's own built-in font, guaranteed present in every player regardless of what fonts the
        // host game ships - safer than guessing which of Silksong's own loaded font asset names to
        // grab (unverified without running the game; if this reads oddly in-game, check what fonts
        // Resources.FindObjectsOfTypeAll<Font>() actually finds loaded and switch to one of those).
        BodyFont = Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    public static int ScaleWidth(int unscaled) => Mathf.RoundToInt(unscaled * Screen.width / 1920f);
    public static int ScaleHeight(int unscaled) => Mathf.RoundToInt(unscaled * Screen.height / 1080f);

    public void Destroy()
    {
        if (SolidSprite != null)
        {
            Object.Destroy(SolidSprite);
        }

        if (SolidTexture != null)
        {
            Object.Destroy(SolidTexture);
        }
    }
}
