using System;
using System.Collections.Generic;
using HarmonyLib;
using InControl;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FsmMaster;

// Click-to-type, Enter-or-click-away-to-confirm text field, including a Harmony patch on
// InControlInputModule.ProcessMove (see FocusOnHoverSuppressionPatch below): focusOnMouseHover
// runs unconditionally for mouse input (gated only on allowMouseInput/Cursor.visible, not on gamepad
// presence), so without this patch, moving the mouse toward another interactive element while a field
// is focused deselects the field mid-click and can swallow the click.
//
// This is a CanvasNode that owns a child InteractiveLabel, rather than a CanvasText subclass with the
// InputField living directly on the same GameObject as the Text component. Unity's
// InputField.UpdateGeometry() lazily creates the caret/highlight visual as a brand-new GameObject
// parented to `textComponent.transform.parent`, then calls `SetAsFirstSibling()` on it. If Text lived
// directly on this node's own GameObject (a sibling of every other row inside the shared "Content"
// scroll panel), that parent would be Content itself - forcing the caret/highlight to sibling index 0
// of Content, rendered behind every row's own background image (added later, hence in front - see
// FsmRightPanel's sibling-order comment), making it permanently invisible regardless of its
// color/alpha. Giving the Text component its own dedicated child GameObject makes
// `textComponent.transform.parent` this node's own GameObject instead, so the injected caret becomes a
// private child scoped to just this one field, matching Unity's own canonical InputField prefab
// layout (background+InputField on the root, Text as a child), and can never be occluded by an
// unrelated row again.
internal sealed class CanvasTextField : CanvasNode
{
    public static bool AnyFieldFocused { get; private set; }

    private readonly UICommon _ui;
    private readonly InteractiveLabel _label;
    private readonly CanvasNode[] _childList;
    private InputField? _inputField;
    private Color _baseColor;

    public event Action<string>? OnSubmit;

    protected override bool Interactable => true;

    public string Text
    {
        get => _label.Text;
        set => _label.Text = value;
    }

    // Backed by _baseColor rather than reading _label.Color directly - the label's actual displayed
    // color is temporarily overridden to black while the user has an active text selection (see
    // UpdateSelectionTextColor), and callers setting/reading Color should see the real per-value-type
    // color they assigned, not whatever's momentarily on screen.
    public Color Color
    {
        get => _baseColor;
        set
        {
            _baseColor = value;
            _label.Color = value;
        }
    }

    public HorizontalWrapMode Overflow
    {
        get => _label.Overflow;
        set => _label.Overflow = value;
    }

    // Exposes the underlying label's Text component read-only, so a caller can measure preferredWidth
    // (see FsmActiveStatePanel's horizontal-scrollbar content-width tracking) without this class having
    // to duplicate that measurement itself.
    public Text? TextComponent => _label.TextComponent;

    public CanvasTextField(string name, UICommon ui) : base(name)
    {
        _ui = ui;
        _label = new InteractiveLabel("Label", ui);
        _label.Parent = this;

        // _label's own constructor already picked up ui.TextColor - mirror that into _baseColor so
        // UpdateSelectionTextColor's per-frame restore (see below) doesn't stomp it back to
        // Color's default (fully transparent black) the instant this field exists.
        _baseColor = _label.Color;

        // Fixed for this node's whole lifetime - built once here instead of a yield-return ChildList()
        // override, which allocated a fresh compiler-generated enumerator on every call (see
        // CanvasNode.ChildList's own comment; this is walked every frame by CollectSubtree).
        _childList = new CanvasNode[] { _label };

        OnUpdate += UpdateSelectionTextColor;
    }

    // Unity's legacy InputField has no built-in notion of a distinct "selected text" color - the glyph
    // color drawn over the selection highlight is still just the label's own color, which can be hard
    // to read against SelectionColor for some of UICommon's own light, pastel value colors
    // (NumericValueColor/StringValueColor/etc.). Force the label to black for as long as a real
    // (non-collapsed) selection exists, and restore its actual color otherwise.
    private void UpdateSelectionTextColor()
    {
        if (_inputField == null)
        {
            return;
        }

        bool hasSelection = IsFocused() && _inputField.selectionAnchorPosition != _inputField.selectionFocusPosition;
        _label.Color = hasSelection ? Color.black : _baseColor;
    }

    protected override IEnumerable<CanvasNode> ChildList() => _childList;

    protected override void OnUpdateSize()
    {
        base.OnUpdateSize();
        _label.Size = Size;
    }

    public override void Build(Transform? rootParent = null)
    {
        base.Build(rootParent);

        _inputField = GameObject!.AddComponent<InputField>();
        _inputField.textComponent = _label.TextComponent;
        _inputField.transition = Selectable.Transition.None;
        _inputField.enabled = false;

        // Engine defaults (a low-alpha light-blue selection, caret tinted to match the text color) are
        // tuned for light-themed UI and are hard to see against UICommon's dark palette.
        _inputField.selectionColor = _ui.SelectionColor;
        _inputField.customCaretColor = true;
        _inputField.caretColor = _ui.CaretColor;
        _inputField.caretWidth = (byte)Mathf.Max(1, UICommon.ScaleWidth(2));

        // This InputField has no separate onSubmit event distinct from onEndEdit (verified via
        // reflection - only onEndEdit/onValueChange/onValueChanged/onValidateInput exist; Enter
        // presses go through the internal SendOnSubmit(), and losing focus any other way goes through
        // OnDeselect(), but both funnel into this same onEndEdit event with the final committed text).
        // So unlike a newer Unity where onSubmit and onEndEdit are distinct events firing back-to-back
        // for an Enter press, there's only one path to handle here.
        _inputField.onEndEdit.AddListener(text =>
        {
            _inputField!.enabled = false;
            Text = text;
            OnSubmit?.Invoke(text);

            AnyFieldFocused = false;
            SetInputLocked(false);
        });

        // Added on this node (the InputField's own root), not on _label directly - a raycast hit on
        // the label's Text graphic still reaches this via Unity's own hierarchy-bubbling pointer-down
        // dispatch (confirmed in UnityEngine.UI.dll: StandaloneInputModule.ProcessMousePress calls
        // ExecuteEvents.ExecuteHierarchy(..., pointerDownHandler), which walks up from the raycast hit
        // through ancestors looking for a handler).
        AddEventTrigger(EventTriggerType.PointerDown, _ =>
        {
            if (!IsFocused())
            {
                Activate();
            }
        });
    }

    public void Activate()
    {
        if (_inputField == null)
        {
            return;
        }

        _inputField.text = Text;
        _inputField.enabled = true;
        _inputField.Select();
        AnyFieldFocused = true;
        SetInputLocked(true);

        // InputField.Select() synchronously fires OnSelect -> ActivateInputField -> SelectAll
        // (confirmed by decompiling UnityEngine.UI.dll), selecting the entire text - collapse that
        // immediately to a caret at the end, in the same call. Deferring the collapse by a frame would
        // leave a window where anything reselecting this field before the deferred frame runs (or a
        // stray extra OnSelect) leaves the select-all state in place with nothing left to collapse it -
        // which reads as "the caret never shows up," since GenerateCaret only ever runs while the
        // selection is already collapsed. Setting caretPosition directly takes effect before this frame
        // renders at all, so there's nothing left to race and no flash of the initial full-text
        // selection to hide.
        _inputField.caretPosition = _inputField.text.Length;
    }

    // No-ops while focused so a background row refresh never overwrites text the user is actively
    // typing. NOT used to revert on a parse failure from within OnSubmit - at that point the field is
    // still focused (onSubmit fires before onEndEdit), so this guard would silently swallow the
    // revert; callers reverting from OnSubmit should assign the plain Text setter instead.
    public void UpdateDefaultText(string text)
    {
        if (IsFocused())
        {
            return;
        }

        Text = text;
        if (_inputField != null)
        {
            _inputField.text = text;
        }
    }

    public bool IsFocused() => _inputField != null && _inputField.enabled;

    public override void Destroy()
    {
        if (IsFocused())
        {
            AnyFieldFocused = false;
            SetInputLocked(false);
        }

        base.Destroy();
    }

    // Unconditional release, for a path where this exact field never gets its own Destroy()/onEndEdit
    // called - GameObject.SetActive(false) on an ancestor (e.g. FsmRightPanel hiding on the
    // toggle-overlay hotkey) does not reliably fire onEndEdit. See FsmRightPanel.OnUpdateActive, the
    // caller of this method.
    public static void ForceReleaseFocus()
    {
        if (AnyFieldFocused)
        {
            AnyFieldFocused = false;
            SetInputLocked(false);
        }
    }

    // Stops typed keystrokes from also driving player movement/actions while a field is focused.
    // InControl.InputManager.Enabled is a real public static settable property directly on this
    // build's Assembly-CSharp.dll (InControl's whole namespace is compiled into it, not shipped as a
    // separate InControl.dll the way it might be on other Unity/InControl setups), so this can be a
    // plain compile-time call rather than a reflection lookup.
    private static void SetInputLocked(bool locked)
    {
        InputManager.Enabled = !locked;
    }
}

// A CanvasText that accepts raycasts - plain CanvasText defaults Interactable to false (correct for
// read-only labels, which should let clicks pass through to whatever's behind them), but
// CanvasTextField's own label is the actual click target that triggers Activate(), so it needs to stay
// a valid raycast target. CanvasNode.Build() adds a blocksRaycasts=false CanvasGroup to any
// non-interactive node, which would otherwise make this label's Text graphic untouchable by
// GraphicRaycaster entirely.
internal sealed class InteractiveLabel : CanvasText
{
    protected override bool Interactable => true;

    public InteractiveLabel(string name, UICommon ui) : base(name, ui)
    {
    }
}

// Suppresses InControlInputModule's "select whatever the mouse is hovering" behavior while any
// CanvasTextField is focused, restoring it immediately afterward. Without this, moving the mouse
// toward another clickable widget (e.g. the Load button) while a field is focused deselects that
// field mid-hover, which can interfere with the click landing on the intended target in the same
// input pass.
[HarmonyPatch(typeof(InControlInputModule), "ProcessMove")]
internal static class FocusOnHoverSuppressionPatch
{
    [HarmonyPrefix]
    private static void Prefix(InControlInputModule __instance, out bool __state)
    {
        __state = __instance.focusOnMouseHover;
        if (CanvasTextField.AnyFieldFocused)
        {
            __instance.focusOnMouseHover = false;
        }
    }

    [HarmonyPostfix]
    private static void Postfix(InControlInputModule __instance, bool __state)
    {
        __instance.focusOnMouseHover = __state;
    }
}
