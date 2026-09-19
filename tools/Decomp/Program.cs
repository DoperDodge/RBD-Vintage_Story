using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.Metadata;

// Decomp <assemblyPath> <FullTypeName> [memberNameFilter]
// Prints decompiled C# for a vanilla type so mod code can be written against real behaviour.
var asmPath = args[0];
var typeName = args[1];
var member = args.Length > 2 ? args[2] : null;

var settings = new DecompilerSettings(LanguageVersion.CSharp10_0) { ThrowOnAssemblyResolveErrors = false };
var dec = new CSharpDecompiler(asmPath, new UniversalAssemblyResolver(asmPath, false, null), settings);
var name = new FullTypeName(typeName);
if (member == null)
{
    Console.WriteLine(dec.DecompileTypeAsString(name));
}
else
{
    var ts = dec.TypeSystem.MainModule.Compilation.FindType(name).GetDefinition();
    if (ts == null) { Console.Error.WriteLine("type not found: " + typeName); return 1; }
    var members = ts.Members.Where(m => m.Name.Contains(member, StringComparison.OrdinalIgnoreCase)).ToList();
    if (members.Count == 0) { Console.Error.WriteLine("no member matching " + member); return 1; }
    Console.WriteLine(dec.DecompileAsString(members.Select(m => m.MetadataToken).ToList()));
}
return 0;
