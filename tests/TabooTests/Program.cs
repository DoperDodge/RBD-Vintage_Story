using System.Text.Json;
using Shinimodori.Taboo;

// Runs tests/TabooCorpus.json through the detector. Exit code 0 only when every
// phrase lands on the right side. §16 Phase 5 demands 100%.
string corpusPath = args.Length > 0 ? args[0] : "tests/TabooCorpus.json";
if (!File.Exists(corpusPath)) { Console.Error.WriteLine($"corpus not found: {corpusPath}"); return 2; }

using var doc = JsonDocument.Parse(File.ReadAllText(corpusPath));
var root = doc.RootElement;

int failures = 0, checkedCount = 0;

void Run(string section, bool expectTrigger)
{
    foreach (var el in root.GetProperty(section).EnumerateArray())
    {
        string text = el.GetString() ?? "";
        var score = TabooDetector.Evaluate(text);
        checkedCount++;
        if (score.Triggered == expectTrigger) continue;
        failures++;
        Console.WriteLine($"FAIL [{section}] expected {(expectTrigger ? "TRIGGER" : "allow")} but got " +
                          $"{(score.Triggered ? "TRIGGER" : "allow")}");
        Console.WriteLine($"      \"{text}\"");
        Console.WriteLine("      " + score.Explain().Replace("\n", "\n      "));
    }
}

Run("trigger", true);
Run("allow", false);

Console.WriteLine();
Console.WriteLine($"{checkedCount - failures}/{checkedCount} phrases correct.");
if (failures > 0) { Console.WriteLine($"{failures} FAILURE(S)"); return 1; }
Console.WriteLine("Taboo corpus passes.");
return 0;
