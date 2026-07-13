using UnityEngine;

namespace FsmMaster;

// A thin (1px) rectangular outline drawn as four separate solid-color strip images. Every strip
// reuses UICommon's one shared solid-color sprite (see CanvasImage), tinted to this border's own
// color, so no border ever needs its own generated texture to track through a ScriptEngine reload.
internal sealed class CanvasBorder : CanvasPanel
{
    private const float Thickness = 1f;

    private readonly CanvasImage _top;
    private readonly CanvasImage _bottom;
    private readonly CanvasImage _left;
    private readonly CanvasImage _right;

    public CanvasBorder(string name, UICommon ui, Color color) : base(name)
    {
        _top = Add(new CanvasImage("Top", ui) { Tint = color });
        _bottom = Add(new CanvasImage("Bottom", ui) { Tint = color });
        _left = Add(new CanvasImage("Left", ui) { Tint = color });
        _right = Add(new CanvasImage("Right", ui) { Tint = color });
    }

    protected override void OnUpdateSize()
    {
        base.OnUpdateSize();
        Layout();
    }

    public override void Build(Transform? rootParent = null)
    {
        base.Build(rootParent);
        Layout();
    }

    private void Layout()
    {
        _top.LocalPosition = Vector2.zero;
        _top.Size = new Vector2(Size.x, Thickness);

        _bottom.LocalPosition = new Vector2(0f, Mathf.Max(0f, Size.y - Thickness));
        _bottom.Size = new Vector2(Size.x, Thickness);

        _left.LocalPosition = Vector2.zero;
        _left.Size = new Vector2(Thickness, Size.y);

        _right.LocalPosition = new Vector2(Mathf.Max(0f, Size.x - Thickness), 0f);
        _right.Size = new Vector2(Thickness, Size.y);
    }
}
