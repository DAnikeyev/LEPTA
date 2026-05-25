using System.Text;

namespace LEPTA.vLLM.Services;

public sealed record VllmAdditionalArgumentsNormalizationResult(
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> Warnings);

public static class VllmAdditionalArgumentsSanitizer
{
    public static VllmAdditionalArgumentsNormalizationResult Normalize(string? value)
    {
        var parsedArguments = Parse(value);
        if (parsedArguments.Count == 0)
        {
            return new VllmAdditionalArgumentsNormalizationResult([], []);
        }

        var normalizedArguments = new List<string>(parsedArguments.Count);
        var warnings = new List<string>();
        var hasExplicitCpuOffloadArgument = parsedArguments.Any(argument =>
            TryMatchOption(argument, "--cpu-offload-gb", out _));

        for (var index = 0; index < parsedArguments.Count; index++)
        {
            var argument = parsedArguments[index];

            if (TryNormalizeSwapSpace(parsedArguments, ref index, argument, normalizedArguments, warnings, hasExplicitCpuOffloadArgument))
            {
                continue;
            }

            if (TryNormalizeDisableLogRequests(parsedArguments, ref index, argument, normalizedArguments, warnings))
            {
                continue;
            }

            if (TryNormalizeEnableLogRequests(parsedArguments, ref index, argument, normalizedArguments, warnings))
            {
                continue;
            }

            normalizedArguments.Add(argument);
        }

        return new VllmAdditionalArgumentsNormalizationResult(
            normalizedArguments,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static IReadOnlyList<string> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var arguments = new List<string>();
        var current = new StringBuilder();
        var inSingleQuotes = false;
        var inDoubleQuotes = false;
        var isEscaping = false;

        static void FlushCurrent(List<string> results, StringBuilder builder)
        {
            if (builder.Length == 0)
            {
                return;
            }

            results.Add(builder.ToString());
            builder.Clear();
        }

        foreach (var character in value)
        {
            if (isEscaping)
            {
                current.Append(character);
                isEscaping = false;
                continue;
            }

            if (inSingleQuotes)
            {
                if (character == '\'')
                {
                    inSingleQuotes = false;
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (inDoubleQuotes)
            {
                if (character == '\\')
                {
                    isEscaping = true;
                }
                else if (character == '"')
                {
                    inDoubleQuotes = false;
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                FlushCurrent(arguments, current);
                continue;
            }

            switch (character)
            {
                case '\'':
                    inSingleQuotes = true;
                    break;
                case '"':
                    inDoubleQuotes = true;
                    break;
                case '\\':
                    isEscaping = true;
                    break;
                default:
                    current.Append(character);
                    break;
            }
        }

        if (isEscaping || inSingleQuotes || inDoubleQuotes)
        {
            throw new InvalidOperationException("Additional vLLM flags contain an unfinished quote or escape sequence.");
        }

        FlushCurrent(arguments, current);
        return arguments;
    }

    private static bool TryNormalizeSwapSpace(
        IReadOnlyList<string> arguments,
        ref int index,
        string argument,
        ICollection<string> normalizedArguments,
        ICollection<string> warnings)
        => TryNormalizeSwapSpace(arguments, ref index, argument, normalizedArguments, warnings, hasExplicitCpuOffloadArgument: false);

    private static bool TryNormalizeSwapSpace(
        IReadOnlyList<string> arguments,
        ref int index,
        string argument,
        ICollection<string> normalizedArguments,
        ICollection<string> warnings,
        bool hasExplicitCpuOffloadArgument)
    {
        if (string.Equals(argument, "--swap-space", StringComparison.OrdinalIgnoreCase))
        {
            if (TryConsumeOptionValue(arguments, ref index, out var swapSpaceValue))
            {
                return TryTranslateSwapSpaceValue(swapSpaceValue, normalizedArguments, warnings, hasExplicitCpuOffloadArgument);
            }

            warnings.Add("Removed deprecated additional vLLM flag '--swap-space' because current vLLM versions no longer accept it.");

            return true;
        }

        if (argument.StartsWith("--swap-space=", StringComparison.OrdinalIgnoreCase))
        {
            var swapSpaceValue = argument[("--swap-space=".Length)..];
            return TryTranslateSwapSpaceValue(swapSpaceValue, normalizedArguments, warnings, hasExplicitCpuOffloadArgument);
        }

        return false;
    }

    private static bool TryTranslateSwapSpaceValue(
        string? swapSpaceValue,
        ICollection<string> normalizedArguments,
        ICollection<string> warnings,
        bool hasExplicitCpuOffloadArgument)
    {
        if (string.IsNullOrWhiteSpace(swapSpaceValue))
        {
            warnings.Add("Removed deprecated additional vLLM flag '--swap-space' because current vLLM versions no longer accept it.");
            return true;
        }

        if (hasExplicitCpuOffloadArgument)
        {
            warnings.Add("Ignored deprecated additional vLLM flag '--swap-space' because '--cpu-offload-gb' is already present.");
            return true;
        }

        normalizedArguments.Add("--cpu-offload-gb");
        normalizedArguments.Add(swapSpaceValue);
        warnings.Add("Translated deprecated additional vLLM flag '--swap-space' to '--cpu-offload-gb' for current vLLM versions.");
        return true;
    }

    private static bool TryNormalizeDisableLogRequests(
        IReadOnlyList<string> arguments,
        ref int index,
        string argument,
        ICollection<string> normalizedArguments,
        ICollection<string> warnings)
    {
        if (!TryMatchOption(argument, "--disable-log-requests", out var inlineValue))
        {
            return false;
        }

        var value = inlineValue;
        if (value is null)
        {
            TryConsumeOptionValue(arguments, ref index, out value);
        }

        if (value is null || TryParseBoolean(value, out var parsedValue) && parsedValue)
        {
            normalizedArguments.Add("--no-enable-log-requests");
            warnings.Add("Translated legacy additional vLLM flag '--disable-log-requests' to '--no-enable-log-requests'.");
            return true;
        }

        if (TryParseBoolean(value, out _))
        {
            warnings.Add("Removed legacy additional vLLM flag '--disable-log-requests false' because current vLLM uses '--enable-log-requests' / '--no-enable-log-requests'.");
            return true;
        }

        warnings.Add("Removed unsupported additional vLLM flag '--disable-log-requests' because current vLLM uses '--enable-log-requests' / '--no-enable-log-requests'.");
        return true;
    }

    private static bool TryNormalizeEnableLogRequests(
        IReadOnlyList<string> arguments,
        ref int index,
        string argument,
        ICollection<string> normalizedArguments,
        ICollection<string> warnings)
    {
        if (!TryMatchOption(argument, "--enable-log-requests", out var inlineValue))
        {
            return false;
        }

        if (inlineValue is null)
        {
            if (!TryConsumeOptionValue(arguments, ref index, out var consumedValue))
            {
                normalizedArguments.Add(argument);
                return true;
            }

            inlineValue = consumedValue;
        }

        if (inlineValue is null || !TryParseBoolean(inlineValue, out var parsedValue))
        {
            normalizedArguments.Add(argument);
            if (inlineValue is not null)
            {
                normalizedArguments.Add(inlineValue);
            }
            return true;
        }

        normalizedArguments.Add(parsedValue ? "--enable-log-requests" : "--no-enable-log-requests");
        warnings.Add("Normalized additional vLLM log-request flag to the boolean syntax expected by current vLLM versions.");
        return true;
    }

    private static bool TryConsumeOptionValue(IReadOnlyList<string> arguments, ref int index, out string? value)
    {
        var nextIndex = index + 1;
        if (nextIndex < arguments.Count && !IsOptionToken(arguments[nextIndex]))
        {
            value = arguments[nextIndex];
            index = nextIndex;
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryMatchOption(string argument, string optionName, out string? inlineValue)
    {
        if (string.Equals(argument, optionName, StringComparison.OrdinalIgnoreCase))
        {
            inlineValue = null;
            return true;
        }

        var prefix = optionName + "=";
        if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            inlineValue = argument[prefix.Length..];
            return true;
        }

        inlineValue = null;
        return false;
    }

    private static bool TryParseBoolean(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                result = true;
                return true;
            case "0":
            case "false":
            case "no":
            case "off":
                result = false;
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static bool IsOptionToken(string value)
        => value.StartsWith("--", StringComparison.Ordinal)
           || (value.StartsWith("-", StringComparison.Ordinal) && value.Length > 1 && !char.IsDigit(value[1]));
}


