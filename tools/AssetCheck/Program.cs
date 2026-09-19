using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Parses every JSON asset with the same library and settings Vintage Story uses,
// so a file that passes here is a file the game will load.
var root = args.Length > 0 ? args[0] : "assets";
int ok = 0, bad = 0;

foreach (var path in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).OrderBy(p => p))
{
    try
    {
        using var reader = new JsonTextReader(new StreamReader(path));
        // The game's asset reader is lenient exactly here: unquoted names,
        // single quotes, trailing commas and // comments are all normal.
        reader.SupportMultipleContent = false;
        var token = JToken.ReadFrom(reader, new JsonLoadSettings
        {
            CommentHandling = CommentHandling.Ignore,
            LineInfoHandling = LineInfoHandling.Load,
        });
        // Make sure nothing trails the root object.
        while (reader.Read()) { }
        Console.WriteLine($"ok    {path}  ({token.Type}, {token.Children().Count()} top-level)");
        ok++;
    }
    catch (Exception e)
    {
        Console.WriteLine($"FAIL  {path}\n        {e.Message}");
        bad++;
    }
}

Console.WriteLine($"\n{ok} ok, {bad} failed");
return bad == 0 ? 0 : 1;
