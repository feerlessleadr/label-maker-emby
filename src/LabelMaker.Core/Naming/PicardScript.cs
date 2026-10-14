using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LabelMaker.Core.Naming
{
    /// <summary>
    /// Thrown when a naming script cannot be read (unknown function, unbalanced brackets). The message says what is wrong so the
    /// settings page can show it.
    /// </summary>
    public sealed class PicardScriptException : Exception
    {
        public PicardScriptException(string message) : base(message) { }
    }

    /// <summary>
    /// Renders a Picard file-naming script: the subset the owner's stock script uses (%variables%, $if, $if2, $and, $or, $gt,
    /// $num). Line breaks in the script are removed, as Picard does, so a multi-line script reads as one line. Variable values
    /// have "/" and "\" replaced, so a value can never create a folder; only the script's own "/" separates folders.
    /// </summary>
    public static class PicardScript
    {
        /// <summary>Checks the script without any tags. Throws <see cref="PicardScriptException"/> when it is not valid.</summary>
        public static void Validate(string script)
        {
            Render(script, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        public static string Render(string script, IReadOnlyDictionary<string, string> variables)
        {
            if (script == null) throw new ArgumentNullException(nameof(script));
            var flat = script.Replace("\r", string.Empty).Replace("\n", string.Empty);
            var parser = new Parser(flat, variables);
            var result = parser.ParseSequence(topLevel: true);
            if (parser.Position != flat.Length)
            {
                throw new PicardScriptException($"Unexpected ')' at position {parser.Position + 1}.");
            }
            return result;
        }

        private sealed class Parser
        {
            private readonly string _text;
            private readonly IReadOnlyDictionary<string, string> _vars;
            public int Position;

            public Parser(string text, IReadOnlyDictionary<string, string> vars)
            {
                _text = text;
                _vars = vars;
            }

            /// <summary>Reads text, variables and calls until the end, or until ',' or ')' when inside a call's arguments.</summary>
            public string ParseSequence(bool topLevel)
            {
                var sb = new StringBuilder();
                while (Position < _text.Length)
                {
                    char c = _text[Position];
                    if (c == ',' || c == ')')
                    {
                        // Inside a call, ',' and ')' end the argument; at the top level ',' is ordinary text and ')' is an error.
                        if (!topLevel || c == ')') return sb.ToString();
                        sb.Append(c);
                        Position++;
                        continue;
                    }
                    if (c == '%')
                    {
                        sb.Append(ReadVariable());
                        continue;
                    }
                    if (c == '$' && Position + 1 < _text.Length && char.IsLetter(_text[Position + 1]))
                    {
                        sb.Append(ReadCall());
                        continue;
                    }
                    sb.Append(c);
                    Position++;
                }
                if (!topLevel) throw new PicardScriptException("A '$' function is missing its closing ')'.");
                return sb.ToString();
            }

            private string ReadVariable()
            {
                int close = _text.IndexOf('%', Position + 1);
                if (close < 0) throw new PicardScriptException($"A variable starting at position {Position + 1} is missing its closing '%'.");
                var name = _text.Substring(Position + 1, close - Position - 1);
                Position = close + 1;
                return _vars.TryGetValue(name, out var value) ? value : string.Empty;
            }

            private string ReadCall()
            {
                int start = Position;
                Position++; // $
                int nameStart = Position;
                while (Position < _text.Length && char.IsLetterOrDigit(_text[Position])) Position++;
                var name = _text.Substring(nameStart, Position - nameStart).ToLowerInvariant();
                if (Position >= _text.Length || _text[Position] != '(')
                {
                    throw new PicardScriptException($"Function \"${name}\" at position {start + 1} needs '(' after its name.");
                }
                Position++; // (

                switch (name)
                {
                    case "if":
                        return ReadIf();
                    case "if2":
                        return ReadIf2();
                    case "and":
                        return ReadMany(name, (values) => AllNonEmpty(values) ? "1" : string.Empty);
                    case "or":
                        return ReadMany(name, (values) => AnyNonEmpty(values) ? "1" : string.Empty);
                    case "gt":
                        return ReadTwo(name, (a, b) => IsGreater(a, b) ? "1" : string.Empty);
                    case "num":
                        return ReadTwo(name, (value, width) => Pad(value, width));
                    default:
                        throw new PicardScriptException($"Function \"${name}\" at position {start + 1} is not known. This version knows $if, $if2, $and, $or, $gt and $num.");
                }
            }

            private string ReadIf()
            {
                // Both branches are read even though only one is used: the inputs are plain variables, so this costs nothing,
                // and a mistake in the unused branch is still reported.
                var cond = ParseSequence(topLevel: false);
                Expect(',', "$if");
                var thenValue = ParseSequence(topLevel: false);
                string elseValue = string.Empty;
                if (Position < _text.Length && _text[Position] == ',')
                {
                    Position++;
                    elseValue = ParseSequence(topLevel: false);
                }
                CloseCall();
                return IsEmpty(cond) ? elseValue : thenValue;
            }

            private string ReadIf2()
            {
                var first = ParseSequence(topLevel: false);
                string second = string.Empty;
                if (Position < _text.Length && _text[Position] == ',')
                {
                    Position++;
                    second = ParseSequence(topLevel: false);
                }
                CloseCall();
                return IsEmpty(first) ? second : first;
            }

            private string ReadMany(string name, Func<List<string>, string> combine)
            {
                var values = new List<string>();
                values.Add(ParseSequence(topLevel: false));
                while (Position < _text.Length && _text[Position] == ',')
                {
                    Position++;
                    values.Add(ParseSequence(topLevel: false));
                }
                CloseCall();
                return combine(values);
            }

            private string ReadTwo(string name, Func<string, string, string> combine)
            {
                var a = ParseSequence(topLevel: false);
                Expect(',', "$" + name);
                var b = ParseSequence(topLevel: false);
                CloseCall();
                return combine(a, b);
            }

            private void Expect(char expected, string context)
            {
                if (Position >= _text.Length || _text[Position] != expected)
                {
                    throw new PicardScriptException($"{context} needs '{expected}' between its arguments.");
                }
                Position++;
            }

            private void CloseCall()
            {
                if (Position >= _text.Length || _text[Position] != ')')
                {
                    throw new PicardScriptException("A '$' function is missing its closing ')'.");
                }
                Position++;
            }

            private static bool IsEmpty(string value) => string.IsNullOrEmpty(value);

            private static bool AllNonEmpty(List<string> values)
            {
                foreach (var value in values) if (IsEmpty(value)) return false;
                return true;
            }

            private static bool AnyNonEmpty(List<string> values)
            {
                foreach (var value in values) if (!IsEmpty(value)) return true;
                return false;
            }

            private static bool IsGreater(string a, string b)
            {
                if (int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                    && int.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
                {
                    return x > y;
                }
                return false;
            }

            /// <summary>Zero-pads a whole number to <paramref name="width"/> digits; any other text is returned as it is.</summary>
            private static string Pad(string value, string width)
            {
                if (!int.TryParse(width, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)) return value;
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0) return value;
                return n.ToString(CultureInfo.InvariantCulture).PadLeft(w, '0');
            }
        }
    }
}
