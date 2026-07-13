using System.Collections.Generic;

namespace FsmMaster;

// Plain container node - concept-ported from Silksong.DebugMod's CanvasPanel
// (agent-context/Silksong.DebugMod-main/UI/Canvas/CanvasPanel.cs), minus its Collapse()/CollapseMode
// GameObject-count optimization: that exists there to flatten hundreds of layout-only wrapper panels
// across DebugMod's much larger settings UI, which FsmMaster's single small right panel doesn't need.
internal class CanvasPanel : CanvasNode
{
    // Bumped by every Add/Remove/ClearChildren across every CanvasPanel in the tree - lets
    // FsmMasterPlugin.Update cache its flattened CollectSubtree result across frames and only recompute
    // it when some panel's child list actually changed (a rare, discrete, user-driven event - a row
    // rebuild, a tab opening/closing), instead of re-walking the whole tree - and re-invoking every
    // composite widget's own ChildList() override, several of which still build their result via a
    // yield-return iterator that allocates a fresh enumerator object per call - every single frame
    // regardless of whether anything about the tree's shape changed since the last one.
    internal static int StructureVersion { get; private set; }

    private readonly List<CanvasNode> _elements = new();
    private readonly Dictionary<string, CanvasNode> _byName = new();

    public CanvasPanel(string name) : base(name)
    {
    }

    protected override IEnumerable<CanvasNode> ChildList() => _elements;

    public T Add<T>(T element) where T : CanvasNode
    {
        element.Parent = this;
        _elements.Add(element);
        _byName[element.Name] = element;
        StructureVersion++;
        return element;
    }

    public T? Get<T>(string name) where T : CanvasNode =>
        _byName.TryGetValue(name, out CanvasNode? node) ? node as T : null;

    public void Remove(CanvasNode element)
    {
        _elements.Remove(element);
        _byName.Remove(element.Name);
        StructureVersion++;
    }

    // Destroys and forgets every current child - used by panels that rebuild their row content
    // wholesale on a selection change (e.g. FsmActiveStatePanel) rather than diffing old vs. new rows.
    public void ClearChildren()
    {
        foreach (CanvasNode child in _elements)
        {
            child.Destroy();
        }

        _elements.Clear();
        _byName.Clear();
        StructureVersion++;
    }

    public override void Destroy()
    {
        base.Destroy();
        _elements.Clear();
        _byName.Clear();
    }
}
