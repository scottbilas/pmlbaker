using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using DebugHelp;

namespace ProcMonUtils
{
    public static class PmlUtils
    {
        // MISSING: support for domain reloads. pass in a timestamp to use (which would in non-test scenarios just come from
        // a stat for create-time on the pmip file itself) and the symbolicator can use the event create time to figure out
        // which pmip set to use.
        // ALSO: probably want to support an automatic `dir $env:temp\pmip*.txt` -> MonoSymbolReader[] 
        
        // OPTZ: don't output events where there are no frames (requires reader to use EventCount and per-event index)
        // OPTZ: use string indexing for compression and speed (symbol names replaced with atoms, table at bottom of file, use more compact easy parse txt representation inline)
        
        public const string k_CaptureTimeFormat = "hh:mm:ss.fffffff tt";
        
        // the purpose of this function is to bake text symbols (for native and mono jit) so the data can be transferred
        // to another machine without needing the exact same binaries and pdb's.
        public static int Symbolicate(string inPmlPath, string? inMonoPmipPath, string outFramesPath, Action<int, int>? progress = null) 
        {
            using var pmlReader = new PmlReader(inPmlPath);

            MonoSymbolReader? monoSymbolReader = null;
            PmlProcess? unityProcess = null;
            
            if (inMonoPmipPath != null)
            {
                monoSymbolReader = new MonoSymbolReader(inMonoPmipPath);
                unityProcess = pmlReader.FindProcessByProcessId(monoSymbolReader.UnityProcessId)
                    ?? throw new FileLoadException($"Unity PID {monoSymbolReader.UnityProcessId} not found in PML processes", inMonoPmipPath);
            }
            
            var tmpFramesPath = outFramesPath + ".tmp";
            using (var framesFile = File.CreateText(tmpFramesPath))
            {
                framesFile.Write("# EVENTS BEGIN\n");
                framesFile.Write("#\n");
                framesFile.Write($"# EventCount = {pmlReader.EventCount}\n");
                framesFile.Write("# EventDoc = Sequence;Time of Day;PID;Frame[0];Frame[1];Frame[..n]\n");
                framesFile.Write("# FrameDoc = $type [$module] $symbol + $offset (type: K=kernel, U=user, M=mono)\n");
                framesFile.Write("#\n");
                
                using var dbghelp = DebugHelp.SymbolHandler.Create();
                var loadedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var addressToSymbols = new Dictionary<ulong, (SymbolInfo s, ulong o)>();
                var dupModuleDetect = new Dictionary<ulong, (PmlProcess p, PmlModule m)>();
                
                var sb = new StringBuilder();
                foreach (var eventStack in pmlReader.SelectEventStacks())
                {
                    sb.Append(eventStack.EventIndex);
                    sb.Append(';');
                    sb.AppendFormat("{0:" + k_CaptureTimeFormat + "}", DateTime.FromFileTime(eventStack.CaptureTime));
                    sb.Append(';');
                    sb.Append(eventStack.Process.ProcessId);
                    sb.Append(';');

                    for (var iframe = 0; iframe < eventStack.FrameCount; ++iframe)
                    {
                        if (iframe != 0)
                            sb.Append(';');
                        
                        var address = eventStack.Frames[iframe];
                        
                        var type = (address & 1UL << 63) != 0 ? 'K' : 'U';
                        
                        var module = eventStack.Process.FindModule(address);
                        var found = false;
                        if (module != null)
                        {
                            if (loadedModules.Add(module.ImagePath))
                            {
                                // TODO: be able to handle separate processes that happen to load modules into overlapping address space..which
                                // could cause multiple potential symbols to resolve for the same address. very slim chance of this i guess?
                                // TODO: more than just base address is important to check..would ideally check the whole range..
                                if (dupModuleDetect.TryGetValue(module.Address.Base, out var dupModule))
                                    throw new Exception("Module collision!");
                                dupModuleDetect.Add(module.Address.Base, (eventStack.Process, module));

                                try
                                {
                                    dbghelp.LoadSymbolsForModule(module.ImagePath, module.Address.Base);
                                    found = true;
                                }
                                catch (Exception e)
                                {
                                    throw new Exception($"Symbol lookup fail for {module.ImagePath} at 0x{address:X}", e);
                                }
                            }

                            if (!addressToSymbols.TryGetValue(address, out var symbol))
                            {
                                try
                                {
                                    var symbolInfo = dbghelp.GetSymbolFromAddress(address, out var offset);
                                    addressToSymbols.Add(address, (symbolInfo, offset));
                                }
                                catch (Win32Exception x) when((uint)x.ErrorCode == 0x80004005 /*E_FAIL*/ &&
                                    (
                                        x.Message.Contains("Attempt to access invalid address") ||
                                        x.Message.Contains("The specified module could not be found")
                                    ))
                                {
                                    // this_is_fine.gif
                                }
                            }
                            
                            if (found)
                                sb.AppendFormat("{0} [{1}] {2} + 0x{3:x}", type, Path.GetFileName(module.ImagePath), symbol.s, symbol.o);
                        }
                        else if (eventStack.Process == unityProcess)
                        {
                            // sometimes can get addresses that seem like they're in the mono jit memory space, but don't
                            // actually match any symbols. don't know why.
                            var monoJit = monoSymbolReader!.FindSymbol(address);
                            if (monoJit != null)
                            {
                                sb.AppendFormat("M [{0}] {1} + 0x{2:x}", monoJit.AssemblyName, monoJit.Symbol, address - monoJit.Address.Base);
                                found = true;
                            }
                        }
                        
                        if (!found)
                            sb.AppendFormat("{0} 0x{1:x}", type, address);
                    }
                    
                    sb.Append('\n');
                    framesFile.Write(sb.ToString());
                    sb.Clear();
                    
                    progress?.Invoke(eventStack.EventIndex, (int)pmlReader.EventCount);
                }
                
                framesFile.Write("#\n");
                framesFile.Write("# EVENTS END\n");
                framesFile.Write("#\n");
            }

            File.Delete(outFramesPath);
            File.Move(tmpFramesPath, outFramesPath);
            
            return (int)pmlReader.EventCount;
        }
    }
}
