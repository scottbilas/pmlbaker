using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DocoptNet;
using NiceIO;
using ProcMonUtils;

const string k_Name = "PML Baker";
const string k_Version = "0.1";
const string k_NtSymbolPath = "_NT_SYMBOL_PATH";

string k_Usage = k_Name + $@"

Tools for baking and querying symbols from a Process Monitor Log (PML) file.

Usage:
  pmlbaker bake [--debug] [--no-ntsymbolpath] [--no-symbol-download] PML
  pmlbaker resolve PML
  pmlbaker query PMLBAKED QUERY...   
  pmlbaker (-h|--help)
  pmlbaker (-v|--version)

  bake      Bake symbols from PML into <PML>.pmlbaked

  resolve   Try to resolve symbols for every module found in PML. If your
            {k_NtSymbolPath} is set to download symbols, this will preload
            PDB's for the PML into your local symbol store.

            Example {k_NtSymbolPath}:
                srv*C:\Symbols*https://msdl.microsoft.com/download/symbols

            Note that the symbol server is fairly slow to download and many
            of the PDB's caught up in a broad capture session can be very
            large. 

  query     Run basic queries against the given PMLBAKED file.     

  PML       Path to the PML (Process Monitor Log) file to process. The folder
            containing this file will also be used for:

            * Finding mono pmip jit log files (copy them here before Unity exits)
            * Writing a .pmlbaked file with the same filename as PML with the
              symbolicated data, to be used for a `query`.

  PMLBAKED  Path to a .pmlbaked file (file extension optional) for running queries.

  QUERY     {{int,datetime}} Print the stack for the matching event.
            {{regex}}        Print ID's for events that match symbol or module name.            

Options:
  --debug               Write PMLBAKED in a more readable text debug format. (3-4x bigger)
  --no-ntsymbolpath     Don't use {k_NtSymbolPath}
  --no-symbol-download  Strip any http* from {k_NtSymbolPath} to avoid slow downloads
  -h --help             Show this screen.
  -v --version          Show version.x

IMPORTANT: baking should be done before any DLL's are replaced, such as from a
Windows Update or rebuilding Unity. No validation is done to ensure that DLL's
matching a given path are the exact same as what was recorded into a PML.
";

var docopt = new Docopt();

var parseOk = true;
docopt.PrintExit += (_, exitArgs) =>
{
    Console.Error.WriteLine(exitArgs.Message);
    parseOk = false;
};

var parsed = docopt.Apply(k_Usage, args, version: $"{k_Name} {k_Version}", exit: true);
if (!parseOk)
    return 1;

if (parsed["bake"].IsTrue)
{
    // FUTURE: this eats up a ton of native memory, probably from loading and using all the PDB's and never unloading them.
    // can probably set up some kind of LRU+pdbsize unload strategy to keep it manageable.
    
    var cancel = false;
    string? currentModule = null;
    
    // yuck, replace this with nice RX
    var monitor = new Thread(() =>
    {
        DateTime? lastModuleUpdateTime = DateTime.Now;
        string? lastModule = null;
        
        while (!cancel)
        {
            var localCurrentModule = currentModule;
            if (localCurrentModule != lastModule)
            {
                lastModuleUpdateTime = DateTime.Now;
                lastModule = localCurrentModule;
            }
            else if (localCurrentModule != null && lastModuleUpdateTime != null)
            {
                var now = DateTime.Now;
                if ((now - lastModuleUpdateTime.Value).TotalSeconds > 2)
                {
                    lastModuleUpdateTime = null;
                    Console.Write($"[loading {localCurrentModule}]");
                }
            }
            
            Thread.Sleep(50);            
        }
    });
    monitor.Start();
    
    string? ntSymbolPath = null;
    if (parsed["--no-ntsymbolpath"].IsTrue)
    {
        ntSymbolPath = "";
    }
    else if (parsed["--no-symbol-download"].IsTrue)
    {
        var oldvar = Environment.GetEnvironmentVariable(k_NtSymbolPath);
        if (oldvar != null)
        {
            var newvar = Regex.Replace(oldvar, @"\bSRV\*([^*]+)\*http[^;]+", "$1", RegexOptions.IgnoreCase);
            if (newvar != oldvar)
            {
                Console.WriteLine($"Replacing {k_NtSymbolPath}: {oldvar} -> {newvar}");
                ntSymbolPath = newvar;
            }
        }
    }
    else if ((Environment.GetEnvironmentVariable(k_NtSymbolPath)?.IndexOf("http") ?? -1) != -1)
        Console.WriteLine($"{k_NtSymbolPath} appears to be set to use a symbol server, which may slow down processing greatly..");  

    var pmlPath = ((string)parsed["PML"].Value).ToNPath().FileMustExist();
    var bakedFile = pmlPath.ChangeExtension(".pmlbaked");

    var iter = 0;
    PmlUtils.Symbolicate(pmlPath, new SymbolicateOptions {
        DebugFormat = parsed["--debug"].IsTrue,
        NtSymbolPath = ntSymbolPath,
        ModuleLoadProgress = name => currentModule = name,
        Progress = (_, total) =>
        {
            if (iter++ == 0)
                Console.Write($"Writing {total} events to {bakedFile.MakeAbsolute()}...");
            else if (iter % 10000 == 0) Console.Write(".");
        }});

    cancel = true;
    Console.WriteLine("done!");
}
else if (parsed["resolve"].IsTrue)
{
    if ((Environment.GetEnvironmentVariable(k_NtSymbolPath)?.IndexOf("http") ?? -1) == -1)
        Console.WriteLine($"{k_NtSymbolPath} appears to be not set to use a symbol server!");  

    Console.Write("Scanning call stacks for modules...");

    var pmlPath = ((string)parsed["PML"].Value).ToNPath().FileMustExist();
    var modulePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var iter = 0;
    using (var pmlReader = new PmlReader(pmlPath))
    {
        foreach (var eventStack in pmlReader.SelectEventStacks())
        {
            foreach (var address in eventStack.Frames)
            {
                if (eventStack.Process.TryFindModule(address, out var module))
                    modulePaths.Add(module.ImagePath);
                
                if (iter++ % 10000000 == 0)
                    Console.Write(".");
            }
        }
    }
    Console.WriteLine("done!");
    
    var dbghelp = new SimpleSymbolHandler();
    foreach (var (modulePath, index) in modulePaths.OrderBy(_ => _).Select((p, i) => (p, b: i)))
    {
        var start = DateTime.Now;
        Console.Write($"{index+1}/{modulePaths.Count} Loading symbols for {modulePath}...");
        try
        {
            dbghelp.LoadModule(modulePath);
            var delta = DateTime.Now - start;
            Console.WriteLine($"done! ({delta.TotalSeconds:0.00}s)");
        }
        catch (Exception e)
        {
            Console.WriteLine($"fail! {e.GetType().Name}: {e.Message}");
        }
    }
}
else if (parsed["query"].IsTrue)
{
    var pmlBakedPath = ((string)parsed["PMLBAKED"].Value).ToNPath();
    if (!pmlBakedPath.HasExtension())
        pmlBakedPath = pmlBakedPath.ChangeExtension(".pmlbaked");
    pmlBakedPath.FileMustExist();

    Console.WriteLine($"Reading {pmlBakedPath}...");
    var pmlQuery = new PmlQuery(pmlBakedPath);

    void Dump(EventRecord eventRecord)
    {
        Console.WriteLine();
        Console.WriteLine("Sequence = " + eventRecord.Sequence);
        Console.WriteLine("CaptureTime = " + eventRecord.CaptureTime.ToString(PmlUtils.CaptureTimeFormat));
        Console.WriteLine("PID = " + eventRecord.ProcessId);

        if (eventRecord.Frames.Length > 0)
        {
            Console.WriteLine("Frames:");
            
            var sb = new StringBuilder();

            for (var i = 0; i < eventRecord.Frames.Length; ++i)
            {
                ref var frame = ref eventRecord.Frames[i];
                sb.Append($"    {i:00} {frame.Type.ToString()[0]}");
                if (frame.ModuleStringIndex != 0)
                    sb.Append($" [{pmlQuery.GetString(frame.ModuleStringIndex)}]");

                sb.Append(frame.SymbolStringIndex != 0
                    ? $" {pmlQuery.GetString(frame.SymbolStringIndex)} + 0x{frame.Offset:x}"
                    : $" 0x{frame.Offset:x}");

                Console.WriteLine(sb);
                sb.Clear();
            }
        }
        else
            Console.WriteLine("Frames: <none>");
    }
    
    foreach (var query in parsed["QUERY"].AsList.Cast<ValueObject>().Select(v => (string)v.Value))
    {
        if (int.TryParse(query, out var eventIdArg))
            Dump(pmlQuery.GetRecordBySequence(eventIdArg));
        else if (DateTime.TryParse(query, out var captureTime))
        {
            var eventRecord = pmlQuery.FindRecordByCaptureTime(captureTime);
            if (eventRecord != null)
                Dump(eventRecord.Value);
            else
                Console.WriteLine("No event found matching " + captureTime.ToString(PmlUtils.CaptureTimeFormat));
        }
        else
        {
            var regex = new Regex(query);
            var eventIds = Enumerable.Concat(
                pmlQuery.MatchRecordsByModule(regex),
                pmlQuery.MatchRecordsBySymbol(regex));
            
            foreach (var eventId in eventIds)
                Dump(pmlQuery.GetRecordBySequence(eventId));
        }
    }
}

return 0;
