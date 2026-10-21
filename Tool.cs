using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
namespace WinDiagnostics;
public static class Tool
{
    public const string Name="win-path-doctor";
    public static readonly string[] ValueOptions=["--command"];
    public static readonly string[] Flags=[];
    public const string Help="win-path-doctor [--command dotnet] [--json]\nInspects process/user/machine PATH and a simplified cmd.exe external-command search. Does not execute commands or inspect PowerShell aliases/functions.";
    public static void ValidateOptions(Options o){var command=o.Get("--command","dotnet");if(!Regex.IsMatch(command,@"\A[A-Za-z0-9_.-]{1,128}\z")||command is "." or "..")throw new ArgumentException("--command must be a bare executable name using letters, digits, dots, underscores, or hyphens.");}
    public static string Normalize(string value)
    {
        value=value.Trim().Trim('"').Replace('/','\\');
        if(Regex.IsMatch(value,@"\A[A-Za-z]:\\\z"))return value.ToUpperInvariant();
        return value.TrimEnd('\\').ToUpperInvariant();
    }
    public static Task<Collection> Collect(Options o,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var issues=new JsonArray();var entries=new JsonArray();
        foreach(var scope in new[]{EnvironmentVariableTarget.Process,EnvironmentVariableTarget.User,EnvironmentVariableTarget.Machine})
        {
            string? raw;
            try{raw=Environment.GetEnvironmentVariable("PATH",scope);}catch(Exception ex)when(ex is System.Security.SecurityException or UnauthorizedAccessException){issues.Add(new JsonObject{["area"]=scope.ToString(),["message"]=ex.Message});continue;}
            int index=0;
            foreach(var item in (raw??"").Split(';'))
            {
                var trimmed=item.Trim().Trim('"');var expanded=Environment.ExpandEnvironmentVariables(trimmed);bool absolute=Path.IsPathFullyQualified(expanded);
                entries.Add(new JsonObject{["scope"]=scope.ToString(),["index"]=index++,["raw"]=item,["expanded"]=expanded,["normalized"]=Normalize(expanded),["exists"]=expanded.Length>0&&Directory.Exists(expanded),["absolute"]=absolute});
            }
        }
        var command=o.Get("--command","dotnet");
        var extensions=(Environment.GetEnvironmentVariable("PATHEXT")??".COM;.EXE;.BAT;.CMD").Split(';').Where(x=>Regex.IsMatch(x,@"\A\.[A-Za-z0-9]+\z")).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
        var candidates=new JsonArray();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var search=new[]{Environment.CurrentDirectory}.Concat(J.Rows(new JsonObject{["entries"]=entries.DeepClone()},"entries").Where(x=>J.Text(x,"scope")=="Process"&&J.Text(x,"expanded").Length>0).Select(x=>J.Text(x,"expanded")));
        bool hasExtension=Path.HasExtension(command);
        foreach(var directory in search)
        {
            ct.ThrowIfCancellationRequested();
            foreach(var suffix in hasExtension?new[]{""}:extensions)
            {
                try
                {
                    var path=Path.GetFullPath(Path.Combine(directory,command+suffix));
                    if(!seen.Add(path))continue;
                    var attributes=File.GetAttributes(path);
                    if(!attributes.HasFlag(FileAttributes.Directory))candidates.Add(new JsonObject{["path"]=path,["rank"]=candidates.Count+1});
                }
                catch(Exception ex)when(ex is FileNotFoundException or DirectoryNotFoundException){}
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){if(issues.Count<20)issues.Add(new JsonObject{["area"]="lookup",["message"]=ex.Message});}
            }
        }
        return Task.FromResult(new Collection(new JsonObject{["command"]=command,["currentDirectory"]=Environment.CurrentDirectory,["pathExt"]=J.Array(extensions),["entries"]=entries,["candidates"]=candidates,["model"]="Current directory, then process PATH, with PATHEXT order; no shell built-ins, aliases, functions, or App Paths."},issues));
    }
    public static void ValidateEvidence(JsonObject e){J.Require(e,"command","entries","candidates","model");J.RequireRows(e,"entries");J.RequireRows(e,"candidates");}
    public static List<Finding> Analyze(JsonObject e)
    {
        var r=new List<Finding>();var entries=J.Rows(e,"entries").ToArray();
        foreach(var group in entries.GroupBy(x=>J.Text(x,"scope")))
        {
            foreach(var duplicate in group.Where(x=>J.Text(x,"expanded").Length>0).GroupBy(x=>Normalize(J.Text(x,"expanded"))).Where(g=>g.Count()>1))r.Add(new("duplicate-path","info","Duplicate PATH directory",$"{group.Key}: {duplicate.Key}","Review duplicates if maintaining PATH; they do not alone establish a lookup failure."));
            foreach(var entry in group.Where(x=>J.Text(x,"expanded").Length>0&&!J.Bool(x,"exists")))r.Add(new("path-unavailable","warning","A PATH directory is missing or inaccessible",$"{group.Key}: {J.Text(entry,"expanded")}","Confirm whether the directory exists and is accessible before editing PATH."));
        }
        if(entries.Any(x=>J.Text(x,"expanded").Length>0&&!J.Bool(x,"absolute")))r.Add(new("relative-path-entry","warning","PATH contains a relative entry","Its meaning depends on the process's working directory.","Review whether an absolute path was intended."));
        if(entries.Any(x=>J.Text(x,"expanded").Length==0))r.Add(new("empty-path-entry","info","PATH contains an empty entry","Empty segments may be ignored or interpreted differently by consumers.","Review the raw scope and index in the JSON evidence."));
        var process=entries.Where(x=>J.Text(x,"scope")=="Process").Select(x=>Normalize(J.Text(x,"expanded"))).ToHashSet();
        if(entries.Any(x=>J.Text(x,"scope")!="Process"&&J.Text(x,"expanded").Length>0&&!process.Contains(Normalize(J.Text(x,"expanded")))))r.Add(new("process-path-difference","info","Process PATH omits a configured directory","The process environment can differ intentionally or reflect a session started before a configuration change.","Compare the JSON scopes and, when appropriate, test from a newly opened terminal."));
        var count=J.Rows(e,"candidates").Count();
        if(count==0)r.Add(new("command-not-found","warning","No external command candidate was found",$"The modeled search did not find {J.Text(e,"command")}.","Check PATH, PATHEXT, and whether the command is a shell alias, function, or built-in."));
        else if(count>1)r.Add(new("command-shadowing","warning","Multiple external command candidates exist","The first modeled match takes priority over later matches. Different shells can resolve commands differently.","Review the ranked candidate paths; do not run an unfamiliar executable just to identify it."));return r;
    }
    public static IEnumerable<string> Describe(JsonObject e){yield return $"Command: {J.Text(e,"command")}";yield return J.Text(e,"model");foreach(var c in J.Rows(e,"candidates"))yield return $"Match {J.Text(c,"rank")}: {J.Text(c,"path")}";foreach(var scope in J.Rows(e,"entries").GroupBy(x=>J.Text(x,"scope")))yield return $"{scope.Key} PATH: {scope.Count()} entries";}
    public static string[] SmokeArgs=>["--command","dotnet"];
}
