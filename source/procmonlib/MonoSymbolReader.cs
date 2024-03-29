﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ProcMonUtils
{
    public class MonoJitSymbol : IAddressRange
    {
        public AddressRange Address;
        
        // mono pmip files sometimes have the symbol portion blank, but we'll keep an entry anyway to track that there
        // is indeed a jit related function there.
        public string? AssemblyName;
        public string? Symbol;
        
        ref readonly AddressRange IAddressRange.AddressRef => ref Address;
    }

    public class MonoSymbolReader
    {
        DateTime m_DomainCreationTime; // use for picking the correct set of jit symbols given the event time
        MonoJitSymbol[] m_Symbols; // keep sorted for bsearch

        public IReadOnlyList<MonoJitSymbol> Symbols => m_Symbols;
        
        public MonoSymbolReader(string monoPmipPath, DateTime? domainCreationTime = null)
        {
            // default to creation time of the pmip as a way to detect domain creation
            
            m_DomainCreationTime = domainCreationTime ?? File.GetCreationTime(monoPmipPath);
            
            // parse pmip
            
            var lines = File.ReadAllLines(monoPmipPath);
            if (lines[0] != "UnityMixedCallstacks:1.0")
                throw new FileLoadException("Mono pmip file has unexpected header or version", monoPmipPath);

            var rx = new Regex(
                @"(?<start>[0-9A-F]{16});"+
                @"(?<end>[0-9A-F]{16});"+
                @"(\[(?<module>([^\]]+))\] (?<symbol>.*))?");
            
            var entries = new List<MonoJitSymbol>();
            
            for (var iline = 1; iline != lines.Length; ++iline)
            {
                var lmatch = rx.Match(lines[iline]);
                if (!lmatch.Success)
                    throw new FileLoadException($"Mono pmip file has unexpected format line {iline}", monoPmipPath);
                
                var addressBase = ulong.Parse(lmatch.Groups["start"].Value, NumberStyles.HexNumber);
                var addressSize = (uint)(ulong.Parse(lmatch.Groups["end"].Value, NumberStyles.HexNumber) - addressBase);
                
                var monoJitSymbol = new MonoJitSymbol
                {
                    Address = new AddressRange(addressBase, addressSize),
                    AssemblyName = lmatch.Groups["module"].Value,
                    Symbol = lmatch.Groups["symbol"].Value
                        .Replace(" (", "(").Replace('/', '.').Replace(':', '.').Replace(",", ", "), // remove mono-isms
                };
                
                // MISSING: handling of a blank assembly+symbol, which *probably* means it's a trampoline
                
                entries.Add(monoJitSymbol);            
            }
            
            m_Symbols = entries.OrderBy(e => e.Address.Base).ToArray();
        }
        
        public bool TryFindSymbol(ulong address, [NotNullWhen(returnValue: true)] out MonoJitSymbol? monoJitSymbol) =>
            m_Symbols.TryFindAddressIn(address, out monoJitSymbol);
        
        public static (int unityPid, int domainSerial) ParsePmipFilename(string monoPmipPath)
        {
            var match = Regex.Match(Path.GetFileName(monoPmipPath), @"^pmip_(?<pid>\d+)_(?<domain>\d+)\.txt$", RegexOptions.IgnoreCase);
            
            if (match.Success &&
                int.TryParse(match.Groups["pid"].Value, out var pid) &&
                int.TryParse(match.Groups["domain"].Value, out var domain))
            {
                return (pid, domain);
            }
            
            throw new FileLoadException("Unable to extract unity PID from mono pmip filename", monoPmipPath);
        }
    }
}
