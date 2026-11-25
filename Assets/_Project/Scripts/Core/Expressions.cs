using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Safehouse.Core
{
    /// <summary>
    /// What an event expression can see. Everything a GM might want to compare: the eight attributes, money,
    /// party size, whether something is carried or already seen, and the dice - which come from the trip's own
    /// generator, so a scene can be replayed exactly.
    /// </summary>
    public sealed class ExpressionContext
    {
        public ExpressionContext(IReadOnlyDictionary<string, int> values, int roubles, int party,
            Func<string, bool> carried = null, Func<string, bool> seen = null, PythonRandom random = null,
            Func<string, bool> hasFlag = null)
        {
            Values = values ?? new Dictionary<string, int>();
            Roubles = roubles;
            Party = party;
            Carried = carried ?? (_ => false);
            Seen = seen ?? (_ => false);
            HasFlag = hasFlag ?? (_ => false);
            Random = random;
        }

        public IReadOnlyDictionary<string, int> Values { get; }
        public int Roubles { get; }
        public int Party { get; }
        public Func<string, bool> Carried { get; }
        public Func<string, bool> Seen { get; }

        /// <summary>Whether the character carries a story flag: the same test a later scene's gate uses.</summary>
        public Func<string, bool> HasFlag { get; }
        public PythonRandom Random { get; }
    }

    /// <summary>
    /// A small arithmetic language for event conditions, compiled once and evaluated many times.
    ///
    /// Grammar (recursive descent, no <c>eval</c> anywhere):
    /// <code>
    /// expr    := or
    /// or      := and ('or' and)*
    /// and     := compare ('and' compare)*
    /// compare := sum (('&lt;'|'&lt;='|'&gt;'|'&gt;='|'=='|'!=') sum)?
    /// sum     := product (('+'|'-') product)*
    /// product := unary (('*'|'/') unary)*
    /// unary   := '-' unary | 'not' unary | primary
    /// primary := number | dice | name | call | '(' expr ')'
    /// dice    := like 1d100, 2d6, d20 - rolled with the trip's generator
    /// call    := carried("item-id") | seen("event-id") | min(a, b) | max(a, b)
    /// </code>
    /// Integer only: comparisons yield 1 or 0, so an expression is usable as a condition by itself.
    /// </summary>
    public static class EventExpression
    {
        private static readonly Dictionary<string, Func<int, int, int>> Functions =
            new Dictionary<string, Func<int, int, int>>
            {
                ["min"] = Math.Min,
                ["max"] = Math.Max,
            };

        /// <summary>Compiles an expression, or throws a ValidationException that names the problem.</summary>
        public static Func<ExpressionContext, int> Compile(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ValidationException("An expression cannot be empty.");
            }

            var parser = new Parser(Tokenize(text), text);
            var compiled = parser.ParseExpression();
            parser.ExpectEnd();
            return compiled;
        }

        /// <summary>Compiles and evaluates in one go. For one-shot checks the compiler cost does not matter.</summary>
        public static int Evaluate(string text, ExpressionContext context) => Compile(text)(context);

        private enum Kind
        {
            Number,
            Dice,
            Name,
            Operator,
            Open,
            Close,
            Comma,
            Text,
            End,
        }

        private readonly struct Token
        {
            public Token(Kind kind, string text, int number)
            {
                TokenKind = kind;
                Text = text;
                Number = number;
            }

            public Kind TokenKind { get; }
            public string Text { get; }
            public int Number { get; }
        }

        private static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            var index = 0;
            while (index < text.Length)
            {
                var ch = text[index];
                if (char.IsWhiteSpace(ch))
                {
                    index++;
                    continue;
                }

                if (ch == '(' || ch == ')')
                {
                    tokens.Add(new Token(ch == '(' ? Kind.Open : Kind.Close, ch.ToString(), 0));
                    index++;
                    continue;
                }

                if (ch == ',')
                {
                    tokens.Add(new Token(Kind.Comma, ",", 0));
                    index++;
                    continue;
                }

                if (ch == '"')
                {
                    var close = text.IndexOf('"', index + 1);
                    if (close < 0)
                    {
                        throw new ValidationException($"Unclosed quote in expression: {text}");
                    }

                    tokens.Add(new Token(Kind.Text, text.Substring(index + 1, close - index - 1), 0));
                    index = close + 1;
                    continue;
                }

                if (char.IsDigit(ch))
                {
                    var start = index;
                    while (index < text.Length && char.IsDigit(text[index]))
                    {
                        index++;
                    }

                    // 2d6: the digit run is a dice count, not a number
                    if (index < text.Length && (text[index] == 'd' || text[index] == 'D'))
                    {
                        index++;
                        var diceStart = index;
                        while (index < text.Length && char.IsDigit(text[index]))
                        {
                            index++;
                        }

                        tokens.Add(new Token(Kind.Dice, text.Substring(start, index - start), 0));
                        continue;
                    }

                    tokens.Add(new Token(Kind.Number, text.Substring(start, index - start),
                        int.Parse(text.Substring(start, index - start), CultureInfo.InvariantCulture)));
                    continue;
                }

                if (char.IsLetter(ch) || ch == '_')
                {
                    var start = index;
                    while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'
                                                   || text[index] == '-'))
                    {
                        index++;
                    }

                    var word = text.Substring(start, index - start);
                    if (word.StartsWith("d", StringComparison.OrdinalIgnoreCase)
                        && word.Length > 1 && word.Skip(1).All(char.IsDigit))
                    {
                        tokens.Add(new Token(Kind.Dice, word, 0));
                        continue;
                    }

                    if (word == "and" || word == "or" || word == "not")
                    {
                        tokens.Add(new Token(Kind.Operator, word, 0));
                        continue;
                    }

                    tokens.Add(new Token(Kind.Name, word, 0));
                    continue;
                }

                var two = index + 1 < text.Length ? text.Substring(index, 2) : "";
                if (two == ">=" || two == "<=" || two == "==" || two == "!=")
                {
                    tokens.Add(new Token(Kind.Operator, two, 0));
                    index += 2;
                    continue;
                }

                if (ch == '<' || ch == '>' || ch == '+' || ch == '-' || ch == '*' || ch == '/')
                {
                    tokens.Add(new Token(Kind.Operator, ch.ToString(), 0));
                    index++;
                    continue;
                }

                throw new ValidationException($"Unexpected character '{ch}' in expression: {text}");
            }

            tokens.Add(new Token(Kind.End, "", 0));
            return tokens;
        }

        private sealed class Parser
        {
            private readonly List<Token> _tokens;
            private readonly string _source;
            private int _position;

            public Parser(List<Token> tokens, string source)
            {
                _tokens = tokens;
                _source = source;
            }

            private Token Current => _tokens[_position];

            private bool Take(string text)
            {
                if (Current.TokenKind != Kind.Operator || Current.Text != text)
                {
                    return false;
                }

                _position++;
                return true;
            }

            public void ExpectEnd()
            {
                if (Current.TokenKind != Kind.End)
                {
                    throw new ValidationException($"Unexpected '{Current.Text}' in expression: {_source}");
                }
            }

            public Func<ExpressionContext, int> ParseExpression() => ParseOr();

            private Func<ExpressionContext, int> ParseOr()
            {
                var left = ParseAnd();
                while (Take("or"))
                {
                    var right = ParseAnd();
                    var previous = left;
                    left = context => previous(context) != 0 | right(context) != 0 ? 1 : 0;
                }

                return left;
            }

            private Func<ExpressionContext, int> ParseAnd()
            {
                var left = ParseCompare();
                while (Take("and"))
                {
                    var right = ParseCompare();
                    var previous = left;
                    left = context => previous(context) != 0 & right(context) != 0 ? 1 : 0;
                }

                return left;
            }

            private Func<ExpressionContext, int> ParseCompare()
            {
                var left = ParseSum();
                foreach (var op in new[] { "<=", ">=", "==", "!=", "<", ">" })
                {
                    if (!Take(op))
                    {
                        continue;
                    }

                    var right = ParseSum();
                    var previous = left;
                    switch (op)
                    {
                        case "<=": return c => previous(c) <= right(c) ? 1 : 0;
                        case ">=": return c => previous(c) >= right(c) ? 1 : 0;
                        case "==": return c => previous(c) == right(c) ? 1 : 0;
                        case "!=": return c => previous(c) != right(c) ? 1 : 0;
                        case "<": return c => previous(c) < right(c) ? 1 : 0;
                        default: return c => previous(c) > right(c) ? 1 : 0;
                    }
                }

                return left;
            }

            private Func<ExpressionContext, int> ParseSum()
            {
                var left = ParseProduct();
                while (true)
                {
                    if (Take("+"))
                    {
                        var right = ParseProduct();
                        var previous = left;
                        left = context => previous(context) + right(context);
                    }
                    else if (Take("-"))
                    {
                        var right = ParseProduct();
                        var previous = left;
                        left = context => previous(context) - right(context);
                    }
                    else
                    {
                        return left;
                    }
                }
            }

            private Func<ExpressionContext, int> ParseProduct()
            {
                var left = ParseUnary();
                while (true)
                {
                    if (Take("*"))
                    {
                        var right = ParseUnary();
                        var previous = left;
                        left = context => previous(context) * right(context);
                    }
                    else if (Take("/"))
                    {
                        var right = ParseUnary();
                        var previous = left;
                        left = context => right(context) == 0 ? 0 : previous(context) / right(context);
                    }
                    else
                    {
                        return left;
                    }
                }
            }

            private Func<ExpressionContext, int> ParseUnary()
            {
                if (Take("-"))
                {
                    var inner = ParseUnary();
                    return context => -inner(context);
                }

                if (Take("not"))
                {
                    var inner = ParseUnary();
                    return context => inner(context) == 0 ? 1 : 0;
                }

                return ParsePrimary();
            }

            private Func<ExpressionContext, int> ParsePrimary()
            {
                var token = Current;
                switch (token.TokenKind)
                {
                    case Kind.Number:
                        _position++;
                        var number = token.Number;
                        return _ => number;

                    case Kind.Dice:
                        _position++;
                        return DiceRoller(token.Text);

                    case Kind.Open:
                        _position++;
                        var inner = ParseExpression();
                        if (Current.TokenKind != Kind.Close)
                        {
                            throw new ValidationException($"Missing ')' in expression: {_source}");
                        }

                        _position++;
                        return inner;

                    case Kind.Name:
                        _position++;
                        return NameOrCall(token.Text);

                    default:
                        throw new ValidationException($"Unexpected '{token.Text}' in expression: {_source}");
                }
            }

            private Func<ExpressionContext, int> NameOrCall(string name)
            {
                if (Current.TokenKind == Kind.Open)
                {
                    _position++;
                    var argument = Current;
                    if (argument.TokenKind != Kind.Text)
                    {
                        throw new ValidationException($"{name}() takes a quoted id: {_source}");
                    }

                    _position++;
                    if (Current.TokenKind != Kind.Close)
                    {
                        throw new ValidationException($"Missing ')' after {name}( in expression: {_source}");
                    }

                    _position++;

                    if (name == "carried")
                    {
                        return context => context.Carried(argument.Text) ? 1 : 0;
                    }

                    if (name == "seen")
                    {
                        return context => context.Seen(argument.Text) ? 1 : 0;
                    }

                    if (name == "flag")
                    {
                        return context => context.HasFlag(argument.Text) ? 1 : 0;
                    }

                    if (Functions.ContainsKey(name))
                    {
                        throw new ValidationException($"{name}() takes two numbers, not an id: {_source}");
                    }

                    throw new ValidationException($"Unknown function '{name}' in expression: {_source}");
                }

                return name switch
                {
                    "roubles" => context => context.Roubles,
                    "party" => context => context.Party,
                    _ => context => context.Values.TryGetValue(name, out var value)
                        ? value
                        : throw new ValidationException($"Unknown value '{name}' in expression."),
                };
            }

            private static Func<ExpressionContext, int> DiceRoller(string text)
            {
                var lower = text.ToLowerInvariant();
                var d = lower.IndexOf('d');
                var count = d == 0 ? 1 : int.Parse(lower.Substring(0, d), CultureInfo.InvariantCulture);
                var sides = int.Parse(lower.Substring(d + 1), CultureInfo.InvariantCulture);
                if (count < 1 || count > 20 || sides < 2 || sides > 1000)
                {
                    throw new ValidationException($"Dice must look like 1d100 (1-20 dice of 2-1000 sides): {text}");
                }

                return context =>
                {
                    if (context.Random == null)
                    {
                        throw new ValidationException("This expression needs dice but no generator was supplied.");
                    }

                    var total = 0;
                    for (var i = 0; i < count; i++)
                    {
                        total += context.Random.RandRange(sides) + 1;
                    }

                    return total;
                };
            }
        }
    }
}
