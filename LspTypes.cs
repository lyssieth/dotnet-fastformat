namespace FastFormat;

internal readonly record struct LspPosition(int Line, int Character);

internal readonly record struct LspRange(LspPosition Start, LspPosition End);

internal sealed class LspTextEdit
{
    public LspTextEdit(LspRange range, string newText)
    {
        Range = range;
        NewText = newText;
    }

    public LspRange Range { get; }
    public string NewText { get; }
}
