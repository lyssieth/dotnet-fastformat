namespace FastFormat.Tests;

public sealed class DocumentTrackerTests
{
    [Fact]
    public void TryGetText_MissingDocument_ReturnsFalse()
    {
        var tracker = new DocumentTracker();

        var found = tracker.TryGetText("file:///missing.cs", out var text);

        Assert.False(found);
        Assert.Null(text);
    }

    [Fact]
    public void OpenThenTryGetText_ReturnsCurrentText()
    {
        var tracker = new DocumentTracker();

        tracker.Open("file:///a.cs", "class C{\n}\n");

        Assert.True(tracker.TryGetText("file:///a.cs", out var text));
        Assert.Equal("class C{\n}\n", text);
    }

    [Fact]
    public void Change_ReplacesWholeDocumentText()
    {
        var tracker = new DocumentTracker();
        tracker.Open("file:///a.cs", "old");

        tracker.Change("file:///a.cs", "new");

        Assert.True(tracker.TryGetText("file:///a.cs", out var text));
        Assert.Equal("new", text);
    }

    [Fact]
    public void Close_RemovesDocument()
    {
        var tracker = new DocumentTracker();
        tracker.Open("file:///a.cs", "old");

        tracker.Close("file:///a.cs");

        Assert.False(tracker.TryGetText("file:///a.cs", out _));
    }
}
