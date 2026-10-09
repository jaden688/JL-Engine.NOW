using Microsoft.CodeAnalysis.Scripting;

namespace JLEngine.Runtime.Tools;

/// <summary>Shared Roslyn ScriptOptions for both forged-tool evaluation and
/// execute_code — every assembly referenced here must correspond to a
/// namespace in Imports, or compilation fails for every script regardless
/// of whether it actually uses that namespace.</summary>
public static class ScriptDefaults
{
    public static readonly ScriptOptions Options = ScriptOptions.Default
        .WithReferences(
            typeof(object).Assembly,               // System.Private.CoreLib
            typeof(Console).Assembly,              // System.Console
            typeof(Dictionary<,>).Assembly,         // System.Collections
            typeof(Enumerable).Assembly)            // System.Linq
        .WithImports("System", "System.Linq", "System.Collections.Generic");
}
