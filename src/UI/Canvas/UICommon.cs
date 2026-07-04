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

    // Toggle-dot indicator sprites (CanvasToggleDot) - a soft-edged filled disc for "on" and a thin
    // outline ring for "off", procedurally generated the same way SolidTexture is rather than baked
    // resource files, so there's nothing extra to ship or track through a ScriptEngine reload beyond
    // the Texture2D/Sprite pairs already disposed in Destroy() below.
    public Texture2D DotFilledTexture { get; }
    public Sprite DotFilledSprite { get; }
    public Texture2D DotRingTexture { get; }
    public Sprite DotRingSprite { get; }

    public Font? BodyFont { get; }
    public Font? HeaderFont { get; }

    public Color PanelBackground { get; } = new(28f / 255f, 30f / 255f, 36f / 255f, 0.92f);
    public Color PanelBorder { get; } = new(70f / 255f, 74f / 255f, 84f / 255f, 1f);
    public Color ButtonNormal { get; } = new(42f / 255f, 45f / 255f, 53f / 255f, 0.95f);
    public Color ButtonActive { get; } = new(58f / 255f, 98f / 255f, 150f / 255f, 0.95f);
    public Color AccentColor { get; } = new(120f / 255f, 170f / 255f, 235f / 255f, 1f);
    public Color TextColor { get; } = new(230f / 255f, 230f / 255f, 235f / 255f, 1f);
    public Color ScrollTrackColor { get; } = new(50f / 255f, 53f / 255f, 61f / 255f, 0.6f);

    // CanvasTextField's InputField selection/caret colors - Unity's own engine defaults (a low-alpha
    // light-blue selection, caret tinted to match the text color) are tuned for light-themed UI and
    // are hard to see against this dark palette. Selection alpha is high (0.7) rather than a
    // typical editor's ~0.3-0.45, since it also has to stay legible under the panel's own pastel
    // per-type value text colors (NumericValueColor/StringValueColor/etc.) - a lighter wash blended
    // into those didn't read as a distinct "this is selected" block.
    public Color SelectionColor { get; } = new(120f / 255f, 170f / 255f, 235f / 255f, 1.0f);
    public Color CaretColor { get; } = new(120f / 255f, 170f / 255f, 235f / 255f, 1f);

    // Type/value color-coding for the Actions/Events/Variables panel (FsmActiveStatePanel) - concept
    // ported from FSMExpress's own type-vs-value color split
    // (agent-context/FSMExpress-master/FSMExpress/Controls/Sidebar/TypeColorConverter.cs), literal
    // values are FsmMaster's own, not copied from its palette.
    public Color TypeBadgeColor { get; } = new(110f / 255f, 196f / 255f, 182f / 255f, 1f);
    public Color NumericValueColor { get; } = new(140f / 255f, 190f / 255f, 225f / 255f, 1f);
    public Color StringValueColor { get; } = new(210f / 255f, 160f / 255f, 120f / 255f, 1f);
    public Color ReadOnlyColor { get; } = new(140f / 255f, 140f / 255f, 145f / 255f, 0.75f);
    public Color DividerColor => PanelBorder;

    // Label color for an action field PlayMaker never serializes/shows in its own editor (private
    // runtime bookkeeping like Wait's startTime/timer) - see FsmActionFieldInfo.IsHidden. A muted
    // violet rather than ReadOnlyColor's gray, since these fields aren't read-only (they use the same
    // editable row types as any other field) and shouldn't look like one at a glance.
    public Color HiddenFieldLabelColor { get; } = new(170f / 255f, 145f / 255f, 210f / 255f, 0.9f);

    public int FontSize => ScaleHeight(13);
    public int HeaderFontSize => ScaleHeight(15);

    public UICommon()
    {
        SolidTexture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        SolidTexture.SetPixel(0, 0, Color.white);
        SolidTexture.Apply();

        SolidSprite = Sprite.Create(SolidTexture, new Rect(0f, 0f, 1f, 1f), Vector2.zero);
        SolidSprite.hideFlags = HideFlags.HideAndDontSave;

        (DotFilledTexture, DotFilledSprite, DotRingTexture, DotRingSprite) = CreateDotSprites();

        // Unity's own built-in font, guaranteed present in every player regardless of what fonts the
        // host game ships - safer than guessing which of Silksong's own loaded font asset names to
        // grab (unverified without running the game; if this reads oddly in-game, check what fonts
        // Resources.FindObjectsOfTypeAll<Font>() actually finds loaded and switch to one of those).
        BodyFont = Resources.GetBuiltinResource<Font>("Arial.ttf");

        // Silksong's own bold display font, matching Silksong.DebugMod's own font-discovery convention
        // (agent-context/Silksong.DebugMod-main/UI/UICommon.cs's LoadResources, which finds this same
        // font name loaded for this same game). Falls back silently to BodyFont if not found - an
        // unverified assumption until confirmed in-game (see this mod's own working notes), but a
        // missing header font is a cosmetic degradation, not a crash.
        HeaderFont = FindLoadedFont("TrajanPro-Bold") ?? BodyFont;
    }

    // Generates the filled-disc/outline-ring pair CanvasToggleDot swaps between, by pixel distance
    // from center - same on-the-fly Texture2D approach as SolidTexture above, just with an alpha
    // mask instead of a flat fill, so both are disposed the same way in Destroy() and neither needs
    // a baked resource file shipped alongside the DLL.
    private static (Texture2D FilledTexture, Sprite FilledSprite, Texture2D RingTexture, Sprite RingSprite) CreateDotSprites()
    {
        const int size = 24;
        const float ringThickness = 2.5f;

        var filledTexture = new Texture2D(size, size) { hideFlags = HideFlags.HideAndDontSave };
        var ringTexture = new Texture2D(size, size) { hideFlags = HideFlags.HideAndDontSave };

        var center = new Vector2(size / 2f, size / 2f);
        float outerRadius = size / 2f - 1f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);

                float filledAlpha = Mathf.Clamp01(outerRadius - dist + 1f);
                float ringAlpha = dist <= outerRadius && dist >= outerRadius - ringThickness ? 1f : 0f;

                filledTexture.SetPixel(x, y, new Color(1f, 1f, 1f, filledAlpha));
                ringTexture.SetPixel(x, y, new Color(1f, 1f, 1f, ringAlpha));
            }
        }

        filledTexture.Apply();
        ringTexture.Apply();

        var filledSprite = Sprite.Create(filledTexture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
        filledSprite.hideFlags = HideFlags.HideAndDontSave;

        var ringSprite = Sprite.Create(ringTexture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
        ringSprite.hideFlags = HideFlags.HideAndDontSave;

        return (filledTexture, filledSprite, ringTexture, ringSprite);
    }

    private static Font? FindLoadedFont(string name)
    {
        foreach (Font font in Resources.FindObjectsOfTypeAll<Font>())
        {
            if (font != null && font.name == name)
            {
                return font;
            }
        }

        return null;
    }

    public static int ScaleWidth(int unscaled) => Mathf.RoundToInt(unscaled * Screen.width / 1920f);
    public static int ScaleHeight(int unscaled) => Mathf.RoundToInt(unscaled * Screen.height / 1080f);

    // Float overloads for layout math that shouldn't round to whole pixels (row heights, indents) -
    // the existing int overloads above are kept as-is for pixel-perfect panel margins/sizes.
    public static float ScaleWidth(float unscaled) => unscaled * Screen.width / 1920f;
    public static float ScaleHeight(float unscaled) => unscaled * Screen.height / 1080f;

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

        if (DotFilledSprite != null)
        {
            Object.Destroy(DotFilledSprite);
        }

        if (DotFilledTexture != null)
        {
            Object.Destroy(DotFilledTexture);
        }

        if (DotRingSprite != null)
        {
            Object.Destroy(DotRingSprite);
        }

        if (DotRingTexture != null)
        {
            Object.Destroy(DotRingTexture);
        }
    }
}
