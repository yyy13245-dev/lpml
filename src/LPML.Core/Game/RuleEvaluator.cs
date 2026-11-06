using LPML.Core.Game.Models;
using System.Runtime.InteropServices;

namespace LPML.Core.Game;

/// <summary>Mojang 规则（allow / deny + os + features）求值。</summary>
public static class RuleEvaluator
{
    public static bool IsAllowed(IEnumerable<Rule>? rules, IDictionary<string, bool>? features = null)
    {
        if (rules is null) return true;
        var allowed = false;
        foreach (var rule in rules)
        {
            if (rule is null) continue;
            if (!MatchOs(rule.Os)) continue;
            if (!MatchFeatures(rule.Features, features)) continue;
            allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    private static bool MatchOs(OsCondition? os)
    {
        if (os is null) return true;
        if (!string.IsNullOrEmpty(os.Name) &&
            !string.Equals(os.Name, "windows", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(os.Arch) &&
            !string.Equals(os.Arch, RuntimeInformation.ProcessArchitecture.ToString(), StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(os.Version))
        {
            try
            {
                var regex = new System.Text.RegularExpressions.Regex(os.Version);
                if (!regex.IsMatch(Environment.OSVersion.VersionString)) return false;
            }
            catch (ArgumentException) { return false; }
        }
        return true;
    }

    private static bool MatchFeatures(Dictionary<string, bool>? required, IDictionary<string, bool>? features)
    {
        if (required is null || required.Count == 0) return true;
        features ??= new Dictionary<string, bool>();
        foreach (var (key, value) in required)
        {
            var has = features.TryGetValue(key, out var actual) && actual;
            if (has != value) return false;
        }
        return true;
    }
}
