using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace FsmMaster;

// One-axis flow layout: positions each appended element end-to-end along the panel's own Horizontal
// axis (or Vertical, if false) while filling the other axis (breadth) to fit within outer/inner
// padding - FsmMaster's replacement for GUILayout.BeginHorizontal/Vertical in the uGUI panel.
// Concept-ported from Silksong.DebugMod's PanelBuilder
// (agent-context/Silksong.DebugMod-main/UI/Canvas/PanelBuilder.cs), trimmed to the slot kinds
// FsmMaster's button row/tab strip actually use (Fixed/Square/Flex/Padding) - no Lazy slots or
// IDisposable Build-on-Dispose sugar, since nothing here needs them yet.
internal sealed class PanelBuilder
{
    private readonly CanvasPanel _panel;
    private readonly ManualLogSource? _logger;
    private readonly List<Entry> _entries = new();

    public bool Horizontal { get; set; }
    public bool DynamicLength { get; set; }
    public float OuterPadding { get; set; }
    public float InnerPadding { get; set; }

    public PanelBuilder(CanvasPanel panel, ManualLogSource? logger = null)
    {
        _panel = panel;
        _logger = logger;
    }

    public T AppendFixed<T>(T element, float length) where T : CanvasNode => Append(element, LengthType.Fixed, length);
    public T AppendSquare<T>(T element) where T : CanvasNode => Append(element, LengthType.Square);
    public T AppendFlex<T>(T element) where T : CanvasNode => Append(element, LengthType.Flex);
    public void AppendPadding(float length) => Append<CanvasNode>(null, LengthType.Fixed, length);

    private T Append<T>(T? element, LengthType type, float length = default) where T : CanvasNode
    {
        _entries.Add(new Entry(element, type, length));

        if (element != null)
        {
            _panel.Add(element);

            float breadth = ChildBreadth();
            if (type == LengthType.Square)
            {
                length = breadth;
            }

            element.Size = Horizontal ? new Vector2(length, breadth) : new Vector2(breadth, length);
        }

        return element!;
    }

    public void Build()
    {
        float totalFixedLength = OuterPadding * 2f + InnerPadding * Mathf.Max(0, _entries.Count - 1);
        int flexCount = 0;

        foreach (Entry entry in _entries)
        {
            switch (entry.Type)
            {
                case LengthType.Fixed:
                    totalFixedLength += entry.Length;
                    break;
                case LengthType.Square:
                    totalFixedLength += ChildBreadth();
                    break;
                case LengthType.Flex:
                    flexCount++;
                    break;
            }
        }

        float flexLength = 0f;
        if (flexCount > 0)
        {
            flexLength = (Length() - totalFixedLength) / flexCount;
            if (Length() < totalFixedLength)
            {
                _logger?.LogWarning($"[FsmMaster] PanelBuilder: '{_panel.Name}' has no room for flex elements; using 0 length.");
                flexLength = 0f;
            }
        }

        float t = OuterPadding;
        foreach (Entry entry in _entries)
        {
            float length = entry.Type switch
            {
                LengthType.Square => ChildBreadth(),
                LengthType.Flex => flexLength,
                _ => entry.Length,
            };

            if (entry.Element != null)
            {
                float x = OuterPadding;
                float y = t;
                float width = ChildBreadth();
                float height = length;
                if (Horizontal)
                {
                    (x, y, width, height) = (y, x, height, width);
                }

                entry.Element.LocalPosition = new Vector2(x, y);
                entry.Element.Size = new Vector2(width, height);
            }

            t += length + InnerPadding;
        }

        t -= InnerPadding;
        t += OuterPadding;

        if (DynamicLength)
        {
            _panel.Size = Horizontal ? new Vector2(t, _panel.Size.y) : new Vector2(_panel.Size.x, t);
        }
    }

    private float Length() => Horizontal ? _panel.Size.x : _panel.Size.y;
    private float Breadth() => Horizontal ? _panel.Size.y : _panel.Size.x;
    private float ChildBreadth() => Breadth() - OuterPadding * 2f;

    private sealed class Entry
    {
        public readonly CanvasNode? Element;
        public readonly LengthType Type;
        public readonly float Length;

        public Entry(CanvasNode? element, LengthType type, float length)
        {
            Element = element;
            Type = type;
            Length = length;
        }
    }

    private enum LengthType
    {
        Fixed,
        Square,
        Flex,
    }
}
