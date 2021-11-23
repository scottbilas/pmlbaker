using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocoptNet;
using NiceIO;
using ProcMonUtils;

const string k_Name = "PML Baker";
const string k_Version = "0.1";
const string k_Usage = k_Name + @"
Bake symbols out for stack frames from a Process Monitor PML (log) file

Usage:
  pmlbaker bake [--debug] [--no-ntsymbolpath] PML
  pmlbaker query PMLBAKED QUERY...   
  pmlbaker (-h|--help)
  pmlbaker (-v|--version)

  PML       Path to the PML (Process Monitor Log) file to process. The folder
            containing this file will also be used for:

              * Finding mono pmip jit log files (copy them here before Unity exits)
              * Writing a .pmlbaked file with the same filename as PML with the
                symbolicated data, to be used for a `query`.

  PMLBAKED  Path to a .pmlbaked file (file extension optional) for running queries.

  QUERY     {int,datetime} Print the stack for the matching event.
            {regex}        Print ID's for events that match symbol or module name.            

Options:
  --debug            Write PMLBAKED in a more readable text debug format. (3-4x bigger)
  --no-ntsymbolpath  Don't use env var _NT_SYMBOL_PATH.
  -h --help          Show this screen.
  -v --version       Show version.
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
    var noNtSymbolPath = parsed["--no-ntsymbolpath"].IsTrue;

    if (!noNtSymbolPath && (Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH")?.IndexOf("http") ?? -1) != -1)
        Console.WriteLine("_NT_SYMBOL_PATH appears to be set to use a symbol server, which may slow down processing greatly..");  
    
    var pmlPath = ((string)parsed["PML"].Value).ToNPath().FileMustExist();
    var bakedFile = pmlPath.ChangeExtension(".pmlbaked");

    var iter = 0;
    PmlUtils.Symbolicate(pmlPath, new SymbolicateOptions {
        DebugFormat = parsed["--debug"].IsTrue,
        NoNtSymbolPath = noNtSymbolPath,
        Progress = (_, total) =>
        {
            if (iter++ == 0)
                Console.Write($"Writing {total} events to {bakedFile.MakeAbsolute()}...");
            else if (iter % 10000 == 0) Console.Write(".");
        }});

    Console.WriteLine("done!");
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
                if (frame.Module != "")
                    sb.Append($" [{frame.Module}]");
                if (frame.Symbol != "")
                    sb.Append($" {frame.Symbol}");
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
