using System.Globalization;

namespace Agentica.Lab.Web.Chat;

// A bounded decimal grammar, not a scripting engine. No names, functions,
// reflection, assignments or host access are accepted.
internal sealed class ExperimentArithmetic(string expression)
{
    private int _position;
    private int _operations;

    public decimal Evaluate()
    {
        if (expression.Length is < 1 or > 512) throw Invalid();
        var result = Sum(0);
        SkipWhitespace();
        if (_position != expression.Length) throw Invalid();
        return result;
    }

    private decimal Sum(int depth)
    {
        var value = Product(depth);
        while (true)
        {
            if (Take('+')) value = checked(value + Product(depth));
            else if (Take('-')) value = checked(value - Product(depth));
            else return value;
            CountOperation();
        }
    }

    private decimal Product(int depth)
    {
        var value = Factor(depth);
        while (true)
        {
            if (Take('*')) value = checked(value * Factor(depth));
            else if (Take('/')) value /= Factor(depth);
            else if (Take('%')) value %= Factor(depth);
            else return value;
            CountOperation();
        }
    }

    private decimal Factor(int depth)
    {
        if (depth >= 32) throw Invalid();
        if (Take('+')) return Factor(depth + 1);
        if (Take('-')) return checked(-Factor(depth + 1));
        if (Take('('))
        {
            var value = Sum(depth + 1);
            if (!Take(')')) throw Invalid();
            return value;
        }
        SkipWhitespace();
        var start = _position;
        while (_position < expression.Length && char.IsAsciiDigit(expression[_position])) _position++;
        if (_position < expression.Length && expression[_position] == '.')
        {
            _position++;
            while (_position < expression.Length && char.IsAsciiDigit(expression[_position])) _position++;
        }
        if (_position < expression.Length && expression[_position] is 'e' or 'E')
        {
            _position++;
            if (_position < expression.Length && expression[_position] is '+' or '-') _position++;
            var exponentStart = _position;
            while (_position < expression.Length && char.IsAsciiDigit(expression[_position])) _position++;
            if (exponentStart == _position) throw Invalid();
        }
        if (!decimal.TryParse(expression.AsSpan(start, _position - start), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var number)) throw Invalid();
        return number;
    }

    private bool Take(char token)
    {
        SkipWhitespace();
        if (_position >= expression.Length || expression[_position] != token) return false;
        _position++;
        return true;
    }

    private void SkipWhitespace()
    {
        while (_position < expression.Length && char.IsWhiteSpace(expression[_position])) _position++;
    }

    private void CountOperation()
    {
        if (++_operations > 128) throw Invalid();
    }

    private static ExperimentToolException Invalid() => new("arithmetic.invalid_expression",
        "Use a bounded decimal expression with numbers, parentheses and + - * / % only.");
}
