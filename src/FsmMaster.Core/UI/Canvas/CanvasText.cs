// SPDX-License-Identifier: EUPL-1.2
using UnityEngine;
using UnityEngine.UI;

namespace FsmMaster;

// Text label wrapper around UnityEngine.UI.Text. The backing field is named _uiText (not `text`) so
// it doesn't collide with the wrapped Text type name.
internal class CanvasText : CanvasNode
{
    protected Text? _uiText;

    // Public so CanvasTextField (a CanvasNode, not a CanvasText subclass - see that file's own header
    // comment for why) can hand this Text component straight to UnityEngine.UI.InputField.textComponent
    // for a child label it owns, rather than duplicating a second Text sibling.
    public Text? TextComponent => _uiText;
    private readonly UICommon _ui;
    private string _text = "";
    private Font? _font;
    private int _fontSize;
    // MiddleLeft (not UpperLeft) so any row/label that never sets Alignment explicitly still reads as
    // vertically centered within whatever height its caller assigned - most single-line rows throughout
    // the panels only ever override this to MiddleCenter, never to change the vertical component.
    private TextAnchor _alignment = TextAnchor.MiddleLeft;
    private Color _color;
    private HorizontalWrapMode _overflow = HorizontalWrapMode.Wrap;
    private FontStyle _fontStyle = FontStyle.Normal;

    protected override bool Interactable => false;

    public CanvasText(string name, UICommon ui) : base(name)
    {
        _ui = ui;
        _font = ui.BodyFont;
        _fontSize = ui.FontSize;
        _color = ui.TextColor;

        // ui.FontSize is a live Screen.height-scaled value (UICommon.ScaleHeight), not a constant -
        // re-synced every frame (mirroring FsmRightPanel/FsmMonitorPanel's own resolution-change
        // reflow) rather than only read once here, since Screen.width/height has been observed
        // returning 0 on the frame this UI is first built (before the game window finishes sizing
        // itself), which would otherwise bake in a permanent near-zero font size that never recovers
        // even after the panel's own geometry reflows to the correct resolution.
        OnUpdate += SyncFontSizeToScale;
    }

    private void SyncFontSizeToScale()
    {
        if (_fontSize != _ui.FontSize)
        {
            FontSize = _ui.FontSize;
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            if (_uiText != null)
            {
                _uiText.text = value;
            }
        }
    }

    public TextAnchor Alignment
    {
        get => _alignment;
        set
        {
            _alignment = value;
            if (_uiText != null)
            {
                _uiText.alignment = value;
            }
        }
    }

    public Color Color
    {
        get => _color;
        set
        {
            _color = value;
            if (_uiText != null)
            {
                _uiText.color = value;
            }
        }
    }

    public int FontSize
    {
        get => _fontSize;
        set
        {
            _fontSize = value;
            if (_uiText != null)
            {
                _uiText.fontSize = value;
            }
        }
    }

    // Wrap (the default) is right for short fixed labels (buttons, headers); single-line data rows
    // (see FsmActiveStatePanel.AddRow) need Overflow instead - a long "Label: Value" string wrapping
    // onto a second visual line would overlap whatever row is positioned right below it, since row
    // positions are laid out assuming a fixed single-line height.
    public HorizontalWrapMode Overflow
    {
        get => _overflow;
        set
        {
            _overflow = value;
            if (_uiText != null)
            {
                _uiText.horizontalOverflow = value;
            }
        }
    }

    // Lets a caller switch to UICommon.HeaderFont for section/action titles - defaults to whatever font
    // the constructor was given (ui.BodyFont).
    public Font? Font
    {
        get => _font;
        set
        {
            _font = value;
            if (_uiText != null)
            {
                _uiText.font = value;
            }
        }
    }

    // Header rows (section titles, action names) use Bold; everything else stays Normal (the default).
    public FontStyle FontStyle
    {
        get => _fontStyle;
        set
        {
            _fontStyle = value;
            if (_uiText != null)
            {
                _uiText.fontStyle = value;
            }
        }
    }

    public override void Build(Transform? rootParent = null)
    {
        base.Build(rootParent);

        _uiText = GameObject!.AddComponent<Text>();
        _uiText.text = _text;
        _uiText.font = _font;
        _uiText.fontSize = _fontSize;
        _uiText.fontStyle = _fontStyle;
        _uiText.alignment = _alignment;
        _uiText.color = _color;
        _uiText.horizontalOverflow = _overflow;
        _uiText.verticalOverflow = VerticalWrapMode.Overflow;
    }
}
