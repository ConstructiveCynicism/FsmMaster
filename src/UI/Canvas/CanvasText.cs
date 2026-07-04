using UnityEngine;
using UnityEngine.UI;

namespace FsmMaster;

// Text label wrapper - concept-ported from Silksong.DebugMod's CanvasText
// (agent-context/Silksong.DebugMod-main/UI/Canvas/CanvasText.cs). Field is named _uiText (not `text`)
// so it doesn't collide with the wrapped UnityEngine.UI.Text type name, matching that same naming
// choice in the reference.
internal class CanvasText : CanvasNode
{
    protected Text? _uiText;

    // Public so CanvasTextField (a CanvasNode, not a CanvasText subclass - see that file's own header
    // comment for why) can hand this Text component straight to UnityEngine.UI.InputField.textComponent
    // for a child label it owns, rather than duplicating a second Text sibling.
    public Text? TextComponent => _uiText;
    private string _text = "";
    private Font? _font;
    private int _fontSize;
    private TextAnchor _alignment = TextAnchor.UpperLeft;
    private Color _color;
    private HorizontalWrapMode _overflow = HorizontalWrapMode.Wrap;
    private FontStyle _fontStyle = FontStyle.Normal;

    protected override bool Interactable => false;

    public CanvasText(string name, UICommon ui) : base(name)
    {
        _font = ui.BodyFont;
        _fontSize = ui.FontSize;
        _color = ui.TextColor;
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
