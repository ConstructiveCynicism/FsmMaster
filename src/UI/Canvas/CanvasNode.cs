using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FsmMaster;

// Base class for every widget in FsmMaster's right-panel uGUI tree - a RectTransform wrapper with
// parent/child bookkeeping. Nothing here is static: ScriptEngine hot-reload tears down and rebuilds
// this tree on every reload, so the tree root drives its own CollectSubtree() walk each frame instead
// of a shared list that would need manual clearing.
internal abstract class CanvasNode
{
    private CanvasNode? _parent;
    private Vector2 _localPosition;
    private Vector2 _size;
    private bool _activeSelf = true;

    protected GameObject? gameObject;
    protected RectTransform? transform;
    private EventTrigger? _eventTrigger;

    public string Name { get; }

    public CanvasNode? Parent
    {
        get => _parent;
        set
        {
            if (_parent != value)
            {
                _parent = value;
                OnUpdateParent();
            }
        }
    }

    public Vector2 LocalPosition
    {
        get => _localPosition;
        set
        {
            if (_localPosition != value)
            {
                _localPosition = value;
                OnUpdateLocalPosition();
            }
        }
    }

    public Vector2 Position => LocalPosition + (Parent?.Position ?? Vector2.zero);

    public Vector2 Size
    {
        get => _size;
        set
        {
            if (_size != value)
            {
                _size = value;
                OnUpdateSize();
            }
        }
    }

    public bool ActiveSelf
    {
        get => _activeSelf;
        set
        {
            if (_activeSelf != value)
            {
                _activeSelf = value;
                OnUpdateActive();
            }
        }
    }

    public bool ActiveInHierarchy => ActiveSelf && (Parent?.ActiveInHierarchy ?? true);

    public GameObject? GameObject => gameObject;

    protected virtual bool Interactable => true;

    public event Action? OnUpdate;

    protected CanvasNode(string name)
    {
        Name = name;
    }

    // ArrayPolyfill.Empty<T>() rather than a yield-return iterator - a yield method allocates a fresh
    // compiler-generated enumerator object on every single call, and this is invoked every frame,
    // for every leaf node, by CollectSubtree below (see that method's own comment on this same
    // cost for the recursion itself).
    protected virtual IEnumerable<CanvasNode> ChildList() => ArrayPolyfill.Empty<CanvasNode>();

    // Appends this node and every descendant into `results` via plain recursion rather than a
    // yield-return iterator. FsmMasterPlugin.Update walks this every single frame against a reused
    // buffer regardless of whether the panel is even visible - the previous nested-iterator version
    // allocated a fresh compiler-generated enumerator object for every node at every level of the
    // recursion on every call, which was a continuous, unconditional source of GC pressure for the
    // entire time the plugin is loaded.
    public void CollectSubtree(List<CanvasNode> results)
    {
        results.Add(this);

        foreach (CanvasNode child in ChildList())
        {
            child.CollectSubtree(results);
        }
    }

    protected virtual void OnUpdateLocalPosition()
    {
        if (gameObject != null)
        {
            UpdateAnchoredPosition();
        }
    }

    protected virtual void OnUpdateSize()
    {
        if (gameObject != null)
        {
            UpdateSizeDelta();
            UpdateClipRect();
        }
    }

    protected virtual void OnUpdateActive()
    {
        if (gameObject != null)
        {
            gameObject.SetActive(ActiveSelf);
        }
    }

    protected virtual void OnUpdateParent()
    {
        if (gameObject != null)
        {
            gameObject.transform.SetParent(GetParentTransform(null), false);
            UpdateClipRect();
        }

        OnUpdateActive();
    }

    // rootParent is only consulted for the tree root (no Parent) - every recursive child Build() call
    // below always already has Parent set (via CanvasPanel.Add), so it never needs it.
    public virtual void Build(Transform? rootParent = null)
    {
        gameObject = new GameObject(Name);
        gameObject.transform.SetParent(GetParentTransform(rootParent), false);

        gameObject.AddComponent<CanvasRenderer>();
        transform = gameObject.AddComponent<RectTransform>();
        transform.anchorMin = transform.anchorMax = new Vector2(0f, 1f);
        transform.pivot = new Vector2(0f, 1f);

        if (!Interactable)
        {
            CanvasGroup group = gameObject.AddComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;
        }

        gameObject.SetActive(ActiveSelf);

        UpdateAnchoredPosition();
        UpdateSizeDelta();
        UpdateClipRect();

        foreach (CanvasNode child in ChildList())
        {
            child.Build();
        }
    }

    private Transform GetParentTransform(Transform? rootParent)
    {
        if (Parent != null)
        {
            return Parent.GameObject!.transform;
        }

        if (rootParent != null)
        {
            return rootParent;
        }

        throw new InvalidOperationException($"Root node '{Name}' has no Parent and no root transform was supplied to Build().");
    }

    private void UpdateAnchoredPosition()
    {
        transform!.anchoredPosition = new Vector2(LocalPosition.x, -LocalPosition.y);
    }

    private void UpdateSizeDelta()
    {
        transform!.sizeDelta = Size;
    }

    // Rect-clips this node's rendering to whatever clip rect its ancestors declare (see
    // CanvasScrollView.GetClipRect) - most nodes never clip (GetClipRect returns false), so this is a
    // no-op for the vast majority of the tree.
    protected virtual bool GetClipRect(out Rect clipRect)
    {
        clipRect = default;
        return false;
    }

    private bool ShouldClip(out Rect clipRect)
    {
        bool clip = GetClipRect(out clipRect);

        if (Parent != null && Parent.ShouldClip(out Rect parentRect))
        {
            clipRect = clip ? Intersect(clipRect, parentRect) : parentRect;
            return true;
        }

        return clip;
    }

    private void UpdateClipRect()
    {
        if (gameObject == null)
        {
            return;
        }

        CanvasRenderer renderer = gameObject.GetComponent<CanvasRenderer>();
        renderer.DisableRectClipping();
        if (ShouldClip(out Rect clipRect))
        {
            renderer.EnableRectClipping(clipRect);
        }
    }

    private static Rect Intersect(Rect a, Rect b)
    {
        float xMin = Mathf.Max(a.xMin, b.xMin);
        float yMin = Mathf.Max(a.yMin, b.yMin);
        float xMax = Mathf.Min(a.xMax, b.xMax);
        float yMax = Mathf.Min(a.yMax, b.yMax);
        return new Rect(xMin, yMin, Mathf.Max(0f, xMax - xMin), Mathf.Max(0f, yMax - yMin));
    }

    public void Update()
    {
        OnUpdate?.Invoke();
    }

    public virtual void Destroy()
    {
        ActiveSelf = false;
        OnUpdate = null;

        foreach (CanvasNode child in ChildList())
        {
            child.Destroy();
        }

        if (gameObject != null)
        {
            UnityEngine.Object.Destroy(gameObject);
            gameObject = null;
        }
    }

    public bool IsMouseOver() => IsPointOver(Input.mousePosition);

    // Position/Size are tracked in this class's own top-left, y-down convention (see
    // UpdateAnchoredPosition's negated Y), but Input.mousePosition (and any other bottom-left/y-up
    // screen point, e.g. a PointerEventData's own position at drag-drop time) is bottom-left, y-up -
    // Screen.height - Position.y - Size.y flips between the two. Factored out of IsMouseOver so
    // drag-and-drop code can hit-test against a specific drop point instead of only "right now."
    public bool IsPointOver(Vector2 screenPoint)
    {
        var bounds = new Rect(Position.x, Screen.height - Position.y - Size.y, Size.x, Size.y);
        if (ShouldClip(out Rect clipRect))
        {
            clipRect = new Rect(clipRect.position + new Vector2(Screen.width / 2f, Screen.height / 2f), clipRect.size);
            bounds = Intersect(bounds, clipRect);
        }

        bounds.x -= 1f;
        bounds.y -= 1f;
        bounds.width += 2f;
        bounds.height += 2f;

        return bounds.Contains(screenPoint);
    }

    public void AddEventTrigger<T>(EventTriggerType type, Action<T> callback) where T : BaseEventData
    {
        if (gameObject == null)
        {
            throw new InvalidOperationException($"Cannot add an event trigger to '{Name}' before Build() has run.");
        }

        _eventTrigger ??= gameObject.AddComponent<EventTrigger>();

        var entry = new EventTrigger.Entry { eventID = type };
        entry.callback.AddListener(data => callback((T)data));
        _eventTrigger.triggers.Add(entry);
    }

    public void AddEventTrigger(EventTriggerType type, Action<PointerEventData> callback) =>
        AddEventTrigger<PointerEventData>(type, callback);
}
