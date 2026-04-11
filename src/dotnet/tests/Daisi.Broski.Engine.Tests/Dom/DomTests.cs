using Daisi.Broski.Engine.Dom;
using Xunit;

namespace Daisi.Broski.Engine.Tests.Dom;

public class DomTests
{
    // -------- construction + ownership --------

    [Fact]
    public void New_document_has_no_children_and_is_its_own_owner()
    {
        var doc = new Document();
        Assert.Null(doc.DocumentElement);
        Assert.False(doc.HasChildNodes);
        Assert.Same(doc, doc.OwnerDocument);
    }

    [Fact]
    public void CreateElement_assigns_owner_document()
    {
        var doc = new Document();
        var el = doc.CreateElement("div");
        Assert.Same(doc, el.OwnerDocument);
        Assert.Equal("div", el.TagName);
        Assert.Equal("DIV", el.NodeName);
        Assert.Equal(NodeType.Element, el.NodeType);
    }

    [Fact]
    public void CreateTextNode_assigns_owner_document_and_sets_data()
    {
        var doc = new Document();
        var t = doc.CreateTextNode("hello");
        Assert.Same(doc, t.OwnerDocument);
        Assert.Equal("hello", t.Data);
        Assert.Equal("#text", t.NodeName);
    }

    // -------- appendChild --------

    [Fact]
    public void AppendChild_sets_parent_and_position()
    {
        var doc = new Document();
        var a = doc.CreateElement("a");
        var b = doc.CreateElement("b");

        doc.AppendChild(a);
        a.AppendChild(b);

        Assert.Same(doc, a.ParentNode);
        Assert.Same(a, b.ParentNode);
        Assert.Same(b, a.FirstChild);
        Assert.Same(a, doc.FirstChild);
    }

    [Fact]
    public void AppendChild_maintains_sibling_pointers()
    {
        var doc = new Document();
        var root = doc.CreateElement("root");
        doc.AppendChild(root);

        var a = doc.CreateElement("a");
        var b = doc.CreateElement("b");
        var c = doc.CreateElement("c");
        root.AppendChild(a);
        root.AppendChild(b);
        root.AppendChild(c);

        Assert.Null(a.PreviousSibling);
        Assert.Same(b, a.NextSibling);
        Assert.Same(a, b.PreviousSibling);
        Assert.Same(c, b.NextSibling);
        Assert.Same(b, c.PreviousSibling);
        Assert.Null(c.NextSibling);
        Assert.Same(a, root.FirstChild);
        Assert.Same(c, root.LastChild);
    }

    [Fact]
    public void AppendChild_removes_child_from_previous_parent()
    {
        var doc = new Document();
        var p1 = doc.CreateElement("p1");
        var p2 = doc.CreateElement("p2");
        var child = doc.CreateElement("c");
        doc.AppendChild(p1);
        doc.AppendChild(p2);
        p1.AppendChild(child);

        p2.AppendChild(child);

        Assert.Same(p2, child.ParentNode);
        Assert.False(p1.HasChildNodes);
        Assert.Same(child, p2.FirstChild);
    }

    [Fact]
    public void AppendChild_refuses_to_create_a_cycle()
    {
        var doc = new Document();
        var a = doc.CreateElement("a");
        var b = doc.CreateElement("b");
        doc.AppendChild(a);
        a.AppendChild(b);

        Assert.Throws<InvalidOperationException>(() => b.AppendChild(a));
    }

    // -------- insertBefore --------

    [Fact]
    public void InsertBefore_places_new_child_in_front_of_reference()
    {
        var doc = new Document();
        var parent = doc.CreateElement("p");
        doc.AppendChild(parent);
        var a = doc.CreateElement("a");
        var c = doc.CreateElement("c");
        parent.AppendChild(a);
        parent.AppendChild(c);

        var b = doc.CreateElement("b");
        parent.InsertBefore(b, c);

        Assert.Equal(3, parent.ChildNodes.Count);
        Assert.Same(a, parent.ChildNodes[0]);
        Assert.Same(b, parent.ChildNodes[1]);
        Assert.Same(c, parent.ChildNodes[2]);

        Assert.Same(a, b.PreviousSibling);
        Assert.Same(c, b.NextSibling);
        Assert.Same(b, a.NextSibling);
        Assert.Same(b, c.PreviousSibling);
    }

    [Fact]
    public void InsertBefore_with_null_reference_acts_like_AppendChild()
    {
        var doc = new Document();
        var parent = doc.CreateElement("p");
        doc.AppendChild(parent);
        var a = doc.CreateElement("a");
        parent.InsertBefore(a, null);

        Assert.Same(a, parent.FirstChild);
    }

    // -------- removeChild --------

    [Fact]
    public void RemoveChild_detaches_and_fixes_siblings()
    {
        var doc = new Document();
        var parent = doc.CreateElement("p");
        doc.AppendChild(parent);
        var a = doc.CreateElement("a");
        var b = doc.CreateElement("b");
        var c = doc.CreateElement("c");
        parent.AppendChild(a);
        parent.AppendChild(b);
        parent.AppendChild(c);

        parent.RemoveChild(b);

        Assert.Null(b.ParentNode);
        Assert.Null(b.PreviousSibling);
        Assert.Null(b.NextSibling);
        Assert.Equal(2, parent.ChildNodes.Count);
        Assert.Same(c, a.NextSibling);
        Assert.Same(a, c.PreviousSibling);
    }

    [Fact]
    public void RemoveChild_of_non_child_throws()
    {
        var doc = new Document();
        var parent = doc.CreateElement("p");
        var orphan = doc.CreateElement("o");
        doc.AppendChild(parent);

        Assert.Throws<InvalidOperationException>(() => parent.RemoveChild(orphan));
    }

    // -------- textContent --------

    [Fact]
    public void TextContent_concatenates_descendant_text_in_document_order()
    {
        var doc = new Document();
        var root = doc.CreateElement("root");
        doc.AppendChild(root);
        root.AppendChild(doc.CreateTextNode("hello "));
        var b = doc.CreateElement("b");
        root.AppendChild(b);
        b.AppendChild(doc.CreateTextNode("world"));
        root.AppendChild(doc.CreateTextNode("!"));

        Assert.Equal("hello world!", root.TextContent);
    }

    [Fact]
    public void TextContent_skips_comment_nodes()
    {
        var doc = new Document();
        var root = doc.CreateElement("r");
        doc.AppendChild(root);
        root.AppendChild(doc.CreateTextNode("a"));
        root.AppendChild(doc.CreateComment("ignored"));
        root.AppendChild(doc.CreateTextNode("b"));

        Assert.Equal("ab", root.TextContent);
    }

    [Fact]
    public void Text_AppendData_concatenates()
    {
        var doc = new Document();
        var t = doc.CreateTextNode("hel");
        t.AppendData("lo");
        t.AppendData(" world");

        Assert.Equal("hello world", t.Data);
    }

    // -------- attributes --------

    [Fact]
    public void SetAttribute_then_GetAttribute_round_trips()
    {
        var doc = new Document();
        var a = doc.CreateElement("a");
        a.SetAttribute("href", "/x");
        a.SetAttribute("id", "main");

        Assert.Equal("/x", a.GetAttribute("href"));
        Assert.Equal("main", a.GetAttribute("id"));
        Assert.Equal("main", a.Id);
        Assert.True(a.HasAttribute("href"));
        Assert.False(a.HasAttribute("missing"));
    }

    [Fact]
    public void SetAttribute_replaces_existing_value_in_place()
    {
        var doc = new Document();
        var a = doc.CreateElement("a");
        a.SetAttribute("href", "/old");
        a.SetAttribute("class", "c1");
        a.SetAttribute("href", "/new");

        Assert.Equal("/new", a.GetAttribute("href"));
        Assert.Equal(2, a.Attributes.Count);
        // Order must be preserved: href stays at index 0 despite replacement.
        Assert.Equal("href", a.Attributes[0].Key);
        Assert.Equal("class", a.Attributes[1].Key);
    }

    [Fact]
    public void RemoveAttribute_returns_true_and_detaches()
    {
        var doc = new Document();
        var a = doc.CreateElement("a");
        a.SetAttribute("id", "x");

        Assert.True(a.RemoveAttribute("id"));
        Assert.False(a.HasAttribute("id"));
        Assert.False(a.RemoveAttribute("id")); // second removal is a no-op
    }

    [Fact]
    public void ClassList_splits_on_whitespace()
    {
        var doc = new Document();
        var a = doc.CreateElement("a");
        a.SetAttribute("class", "  foo bar\tbaz\n ");

        var classes = a.ClassList;
        Assert.Equal(["foo", "bar", "baz"], classes);
    }

    // -------- traversal + lookup --------

    [Fact]
    public void GetElementById_walks_the_tree()
    {
        var doc = new Document();
        var root = doc.CreateElement("root");
        doc.AppendChild(root);
        var nested = doc.CreateElement("nested");
        nested.SetAttribute("id", "target");
        var leaf = doc.CreateElement("leaf");
        root.AppendChild(nested);
        nested.AppendChild(leaf);

        Assert.Same(nested, doc.GetElementById("target"));
        Assert.Null(doc.GetElementById("missing"));
    }

    [Fact]
    public void GetElementsByTagName_filters_by_tag()
    {
        var doc = new Document();
        var root = doc.CreateElement("root");
        doc.AppendChild(root);
        root.AppendChild(doc.CreateElement("p"));
        root.AppendChild(doc.CreateElement("div"));
        root.AppendChild(doc.CreateElement("p"));

        var ps = doc.GetElementsByTagName("p").ToList();
        Assert.Equal(2, ps.Count);
    }

    [Fact]
    public void GetElementsByClassName_matches_any_whitespace_token()
    {
        var doc = new Document();
        var root = doc.CreateElement("root");
        doc.AppendChild(root);
        var a = doc.CreateElement("a");
        a.SetAttribute("class", "primary button");
        var b = doc.CreateElement("a");
        b.SetAttribute("class", "button");
        var c = doc.CreateElement("a");
        c.SetAttribute("class", "primary");
        root.AppendChild(a);
        root.AppendChild(b);
        root.AppendChild(c);

        var buttons = doc.GetElementsByClassName("button").ToList();
        Assert.Equal(2, buttons.Count);
        Assert.Contains(a, buttons);
        Assert.Contains(b, buttons);
    }

    // -------- Document convenience getters --------

    [Fact]
    public void Head_and_Body_are_found_when_present()
    {
        var doc = new Document();
        var html = doc.CreateElement("html");
        var head = doc.CreateElement("head");
        var body = doc.CreateElement("body");
        doc.AppendChild(html);
        html.AppendChild(head);
        html.AppendChild(body);

        Assert.Same(html, doc.DocumentElement);
        Assert.Same(head, doc.Head);
        Assert.Same(body, doc.Body);
    }

    [Fact]
    public void Doctype_is_retrieved_from_document_children()
    {
        var doc = new Document();
        var dt = doc.CreateDocumentType("html");
        doc.AppendChild(dt);
        doc.AppendChild(doc.CreateElement("html"));

        Assert.Same(dt, doc.Doctype);
    }

    // -------- ownerDocument propagation --------

    [Fact]
    public void OwnerDocument_propagates_through_appended_subtree()
    {
        var doc = new Document();
        // Build a subtree disconnected from the document.
        var a = new Element("a");
        var b = new Element("b");
        a.AppendChild(b);
        // a and b have no owner yet.
        Assert.Null(a.OwnerDocument);
        Assert.Null(b.OwnerDocument);

        doc.AppendChild(a);

        // Both a and its descendant b now belong to the document.
        Assert.Same(doc, a.OwnerDocument);
        Assert.Same(doc, b.OwnerDocument);
    }
}
