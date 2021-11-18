using System;
using DocoptNet;
using NiceIO;
using ProcMonUtils;

const string k_Name = "PML Baker";
const string k_Version = "0.1";
const string k_Usage = k_Name + @"
Bake symbols out for stack frames from a Process Monitor PML (log) file

Usage:
  pmlbaker PML [PMIP]
  pmlbaker (-h|--help)
  pmlbaker (-v|--version)

  PML is the path to the PML file to process. A .pmlbaked file will be written next to it.
  PMIP is the path to the mono-jit log (pmip_<pid>_<did>.txt you copied from %TEMP%)

Options:
  -h --help           Show this screen.
  -v --version        Show version.
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

var pmlPath = ((string)parsed["PML"].Value).ToNPath().FileMustExist();
var pmipPath = ((string?)parsed["PMIP"]?.Value)?.ToNPath()?.FileMustExist();

var bakedFile = pmlPath.ChangeExtension(".pmlbaked");
var written = PmlUtils.Symbolicate(pmlPath, pmipPath?.ToString(), bakedFile);

Console.WriteLine($"Wrote {written} events to {bakedFile.MakeAbsolute()}");

return 0;
