using System;
using System.Text;
using System.Management.Automation.Language;

namespace TerminalApp;

public static class AnsiHighlighter
{
    // ANSI SGR reset
    private const string Reset = "\x1b[0m";

    public static string Highlight(string code)
    {
        if (string.IsNullOrEmpty(code)) return "";

        Parser.ParseInput(code, out Token[] tokens, out _);

        var highlighted = new StringBuilder();
        int lastIndex = 0;

        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.EndOfInput) continue;

            // Gap between last token and this one (whitespace, etc.)
            int start = token.Extent.StartOffset;
            if (start > lastIndex)
                highlighted.Append(code.Substring(lastIndex, start - lastIndex));

            string style = GetStyleForToken(token);
            if (!string.IsNullOrEmpty(style))
            {
                highlighted.Append($"\x1b[{style}m");
                highlighted.Append(token.Text);
                highlighted.Append(Reset);
            }
            else
            {
                highlighted.Append(token.Text);
            }

            lastIndex = token.Extent.EndOffset;
        }

        // Any trailing text not covered by tokens
        if (lastIndex < code.Length)
            highlighted.Append(code.Substring(lastIndex));

        return highlighted.ToString();
    }

    private static string GetStyleForToken(Token token)
    {
        // String literals — green
        if (token.Kind == TokenKind.StringLiteral ||
            token.Kind == TokenKind.StringExpandable ||
            token.Kind == TokenKind.HereStringLiteral ||
            token.Kind == TokenKind.HereStringExpandable)
            return "38;5;78";

        // Keywords (if, else, foreach, function, param, return, ...) — hot pink
        if (token.TokenFlags.HasFlag(TokenFlags.Keyword))
            return "38;5;205";

        // Variables ($foo, $Global:Bar) — golden yellow
        if (token.Kind == TokenKind.Variable || token.Kind == TokenKind.SplattedVariable)
            return "38;5;220";

        // Parameters (-Force, -Recurse) — neon orange
        if (token.Kind == TokenKind.Parameter)
            return "38;5;208";

        // Numbers — lavender
        if (token.Kind == TokenKind.Number)
            return "38;5;141";

        // Comments — dim italic gray
        if (token.Kind == TokenKind.Comment)
            return "38;5;244;3";

        // Operators (|, +, -eq, -and, ...) — steel blue
        if (token.TokenFlags.HasFlag(TokenFlags.BinaryOperator) ||
            token.TokenFlags.HasFlag(TokenFlags.UnaryOperator))
            return "38;5;75";

        // Command names / identifiers / cmdlets — sky blue
        if (token.TokenFlags.HasFlag(TokenFlags.CommandName) ||
            token.Kind == TokenKind.Identifier)
            return "38;5;81";

        // Type literals ([String], [int]) — teal
        if (token.Kind == TokenKind.LBracket && token.TokenFlags.HasFlag(TokenFlags.TypeName))
            return "38;5;43";

        return "";
    }
}