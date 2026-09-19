using System.Reflection;

// Dumps the public/protected surface of the Vintage Story assemblies so that every
// API used by the mod can be verified against the real installed version.
//   ApiDump <vsdir> <outfile> [typeNameFilterRegex]
var vsDir  = args.Length > 0 ? args[0] : "/opt/vs";
var outFile= args.Length > 1 ? args[1] : "/tmp/api.txt";
var filter = args.Length > 2 ? new System.Text.RegularExpressions.Regex(args[2],
                System.Text.RegularExpressions.RegexOptions.IgnoreCase) : null;

var files = new List<string>();
files.AddRange(Directory.GetFiles(vsDir, "*.dll"));
files.AddRange(Directory.GetFiles(Path.Combine(vsDir, "Mods"), "*.dll"));
files.AddRange(Directory.GetFiles(Path.Combine(vsDir, "Lib"), "*.dll"));
var runtime = Directory.GetFiles(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "*.dll");
files.AddRange(runtime);

var resolver = new PathAssemblyResolver(files.Distinct());
using var mlc = new MetadataLoadContext(resolver, typeof(object).Assembly.GetName().Name);

string Fmt(Type t)
{
    if (t == null) return "?";
    if (t.IsByRef) return "ref " + Fmt(t.GetElementType());
    if (t.IsArray) return Fmt(t.GetElementType()) + "[]";
    if (t.IsGenericType)
    {
        var n = t.Name.Contains('`') ? t.Name[..t.Name.IndexOf('`')] : t.Name;
        return n + "<" + string.Join(",", t.GetGenericArguments().Select(Fmt)) + ">";
    }
    return t.Name;
}
string P(ParameterInfo p)
{
    var mod = p.IsOut ? "out " : (p.ParameterType.IsByRef ? "ref " : "");
    var ty  = p.ParameterType.IsByRef ? Fmt(p.ParameterType.GetElementType()) : Fmt(p.ParameterType);
    var dflt= p.HasDefaultValue ? " = " + (p.RawDefaultValue?.ToString() ?? "null") : "";
    return $"{mod}{ty} {p.Name}{dflt}";
}

using var w = new StreamWriter(outFile);
var targets = new[] { "VintagestoryAPI.dll", "VintagestoryLib.dll", "VSSurvivalMod.dll", "VSEssentials.dll", "VSCreativeMod.dll" };
foreach (var asmFile in targets)
{
    var full = files.FirstOrDefault(f => Path.GetFileName(f) == asmFile);
    if (full == null) continue;
    Assembly asm;
    try { asm = mlc.LoadFromAssemblyPath(full); } catch (Exception e) { w.WriteLine($"!! {asmFile}: {e.Message}"); continue; }
    Type[] types;
    try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
    foreach (var t in types.Where(t => t != null && t.IsPublic || (t?.IsNestedPublic ?? false)).OrderBy(t => t!.FullName))
    {
        if (filter != null && !filter.IsMatch(t!.FullName ?? "")) continue;
        try {
        var kind = t!.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : "class";
        var bases = new List<string>();
        if (t.BaseType != null && t.BaseType.Name != "Object" && t.BaseType.Name != "ValueType" && t.BaseType.Name != "Enum") bases.Add(Fmt(t.BaseType));
        try { bases.AddRange(t.GetInterfaces().Select(Fmt)); } catch { }
        w.WriteLine($"\n===== [{asmFile}] {kind} {t.FullName}{(bases.Count > 0 ? " : " + string.Join(", ", bases.Distinct()) : "")}");
        const BindingFlags BF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        if (t.IsEnum)
        {
            foreach (var f in t.GetFields(BF).Where(f => f.IsLiteral))
                w.WriteLine($"    {f.Name} = {f.GetRawConstantValue()}");
            continue;
        }
        try {
        foreach (var f in t.GetFields(BF).Where(f => f.IsPublic || f.IsFamily))
            { try { w.WriteLine($"    {(f.IsStatic ? "static " : "")}field {Fmt(f.FieldType)} {f.Name}"); } catch { } }
        } catch { }
        try {
        foreach (var p in t.GetProperties(BF))
        {
          try {
            var g = p.GetMethod; var s = p.SetMethod;
            if (!((g?.IsPublic ?? false) || (g?.IsFamily ?? false) || (s?.IsPublic ?? false) || (s?.IsFamily ?? false))) continue;
            w.WriteLine($"    {((g ?? s)!.IsStatic ? "static " : "")}prop {Fmt(p.PropertyType)} {p.Name} {{ {(g != null ? "get; " : "")}{(s != null ? "set; " : "")}}}");
          } catch { }
        }
        } catch { }
        try {
        foreach (var ev in t.GetEvents(BF))
            { try { w.WriteLine($"    event {Fmt(ev.EventHandlerType)} {ev.Name}"); } catch { } }
        } catch { }
        try {
        foreach (var m in t.GetMethods(BF).Where(m => (m.IsPublic || m.IsFamily) && !m.IsSpecialName).OrderBy(m => m.Name))
            { try { w.WriteLine($"    {(m.IsStatic ? "static " : "")}{(m.IsVirtual && !m.IsFinal ? "virtual " : "")}{Fmt(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(P))})"); } catch { } }
        } catch { }
        try {
        foreach (var c in t.GetConstructors(BF).Where(c => c.IsPublic || c.IsFamily))
            { try { w.WriteLine($"    ctor {t.Name}({string.Join(", ", c.GetParameters().Select(P))})"); } catch { } }
        } catch { }
        } catch (Exception ex) { w.WriteLine($"!! type {t?.FullName}: {ex.GetType().Name}"); }
    }
}
Console.WriteLine("dumped -> " + outFile);
