using Daisi.Broski.Engine.Dom;

namespace Daisi.Broski.Engine.Layout;

/// <summary>
/// One node in the layout tree. Holds the computed box-model
/// rects relative to the document origin: <see cref="X"/> /
/// <see cref="Y"/> are the top-left of the *border box*, and
/// <see cref="Width"/> / <see cref="Height"/> measure the
/// content area only — padding and border push outward
/// (content-box sizing model, the CSS 2.1 default).
///
/// <para>
/// One <see cref="LayoutBox"/> per styled element that
/// participates in flow. Anonymous wrapper boxes that real
/// browsers create for inline-only content aren't modeled
/// in 6c — that's part of inline flow (slice 6d).
/// </para>
/// </summary>
public sealed class LayoutBox
{
    /// <summary>The DOM element this box was generated from.
    /// Null for the synthetic root box that wraps the
    /// viewport.</summary>
    public Element? Element { get; init; }

    /// <summary>How the box participates in flow: block (the
    /// only supported mode in 6c), inline (treated as block
    /// for layout, will be split into line boxes in 6d), or
    /// none (skipped during layout, present only so the
    /// tree structure can be inspected).</summary>
    public BoxDisplay Display { get; init; }

    public double X { get; internal set; }
    public double Y { get; internal set; }
    public double Width { get; internal set; }
    public double Height { get; internal set; }

    public BoxEdges Margin { get; internal set; }
    public BoxEdges Padding { get; internal set; }
    public BoxEdges Border { get; internal set; }

    public List<LayoutBox> Children { get; } = new();

    /// <summary>The border-box outer rectangle in document
    /// coordinates. <c>getBoundingClientRect</c> returns this
    /// (same coords because the engine's viewport scrollX /
    /// scrollY are both zero in headless mode).</summary>
    public LayoutRect BorderBoxRect => new(
        X - Padding.Left - Border.Left,
        Y - Padding.Top - Border.Top,
        Width + Padding.Left + Padding.Right + Border.Left + Border.Right,
        Height + Padding.Top + Padding.Bottom + Border.Top + Border.Bottom);

    /// <summary>Walk every descendant box (depth-first, self-
    /// excluded). Used by <see cref="LayoutTree.Find"/> to
    /// locate the box for a given element.</summary>
    public IEnumerable<LayoutBox> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var grand in child.Descendants()) yield return grand;
        }
    }
}

/// <summary>Edge offsets for margin / padding / border. All
/// in absolute pixels resolved against the containing block.
/// Default-constructed = all zero, matching the CSS initial
/// value.</summary>
public readonly struct BoxEdges
{
    public double Top { get; }
    public double Right { get; }
    public double Bottom { get; }
    public double Left { get; }

    public BoxEdges(double top, double right, double bottom, double left)
    {
        Top = top;
        Right = right;
        Bottom = bottom;
        Left = left;
    }

    public static BoxEdges Uniform(double v) => new(v, v, v, v);
    public static readonly BoxEdges None = default;
}

public readonly record struct LayoutRect(double X, double Y, double Width, double Height)
{
    public double Top => Y;
    public double Left => X;
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

public enum BoxDisplay
{
    /// <summary>Element is hidden — no layout, no box, but
    /// still present in the tree so subsequent siblings know
    /// the slot was skipped.</summary>
    None,
    /// <summary>Block-level box — full available width by
    /// default, stacks vertically with siblings.</summary>
    Block,
    /// <summary>Inline-level box — phase 6c treats these as
    /// block for layout. Phase 6d will split them into line
    /// boxes inside the parent block formatting context.</summary>
    Inline,
    /// <summary>Inline-block — shrink-wraps content like
    /// inline but participates in block flow as a single
    /// box. Phase 6c also treats this as block.</summary>
    InlineBlock,
}
