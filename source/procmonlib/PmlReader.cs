using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;

// PML format: https://github.com/eronnen/procmon-parser/blob/master/docs/PML%20Format.md
// consts.py: https://github.com/eronnen/procmon-parser/blob/master/procmon_parser/consts.py
    
namespace ProcMonUtils
{
    [DebuggerDisplay("{ProcessName}")]
    public class PmlProcess
    {
        public readonly uint ProcessId;
        public readonly string ProcessName;
        public IReadOnlyList<PmlModule> Modules => m_Modules;

        readonly PmlModule[] m_Modules;

        public PmlProcess(uint processId, string processName, PmlModule[] takeModules)
        {
            ProcessId = processId;
            ProcessName = processName;
            m_Modules = takeModules;

            // keep sorted for bsearch
            Array.Sort(m_Modules, (a, b) => a.Address.Base.CompareTo(b.Address.Base));
        }
        
        public bool TryFindModule(ulong address, [NotNullWhen(returnValue: true)] out PmlModule? module) =>
            m_Modules.TryFindAddressIn(address, out module);
    }

    [DebuggerDisplay("{ImagePath}")]
    public class PmlModule : IAddressRange
    {
        public readonly string ImagePath;
        public readonly string ModuleName;
        public readonly AddressRange Address;

        public PmlModule(string imagePath, AddressRange address)
        {
            ImagePath = imagePath;
            ModuleName = Path.GetFileName(ImagePath);
            Address = address;
        }
        
        ref readonly AddressRange IAddressRange.AddressRef => ref Address;
    }

    [DebuggerDisplay("#{EventIndex} ({FrameCount} frames)")]
    public class PmlEventStack
    {
        public int EventIndex;
        public long CaptureTime; // FILETIME
        public PmlProcess Process = null!;
        public ulong[] Frames = new ulong[200];
        public int FrameCount;
    }

    // from consts.py
    public enum PmlEventClass
    {
        Unknown = 0,
        Process = 1,
        Registry = 2,
        FileSystem = 3,
        Profiling = 4,
        Network = 5,
    }
    
    public class PmlReader : IDisposable
    {
        BinaryReader m_Reader;
        uint m_EventCount;
        ulong m_EventOffsetsOffset;
        Dictionary<uint, PmlProcess> m_ProcessesByPmlIndex = new();
        string[] m_Strings;

        public void Dispose() => m_Reader.Dispose();

        public uint EventCount => m_EventCount;

        public PmlReader(string pmlPath)
        {
            m_Reader = new BinaryReader(new FileStream(pmlPath, FileMode.Open, FileAccess.Read, FileShare.Read), Encoding.Unicode);
            
            if (m_Reader.ReadByte() != 'P' || // Signature - "PML_"
                m_Reader.ReadByte() != 'M' ||
                m_Reader.ReadByte() != 'L' ||
                m_Reader.ReadByte() != '_')
            {
                throw new FileLoadException("Not a PML file", pmlPath);
            }
            
            const int expectedVersion = 9;
            var version = m_Reader.ReadUInt32();
            if (version != expectedVersion) // The version of the PML file. I assume its 9
                throw new FileLoadException($"PML has version {version}, expected {expectedVersion}", pmlPath);
            
            var is64Bit = m_Reader.ReadUInt32(); // 1 if the system is 64 bit, 0 otherwise
            if (is64Bit != 1)
                throw new FileLoadException($"PML must be 64-bit", pmlPath);
            
            SeekCurrent(
                0x20 +  // The computer name
                0x208); // The system root path (like "C:\Windows")

            m_EventCount = m_Reader.ReadUInt32();
            
            SeekCurrent(8); // Unknown
            var eventsDataOffset = m_Reader.ReadUInt64();
            m_EventOffsetsOffset = m_Reader.ReadUInt64();

            // don't pskill your procmon or it won't update the header; use /terminate instead
            if (m_EventOffsetsOffset == 0)
                throw new FileLoadException($"File was not closed cleanly during capture and is corrupt", pmlPath);
            
            var processesOffset = m_Reader.ReadUInt64();
            var stringsOffset = m_Reader.ReadUInt64();

            // minor sanity check
            SeekBegin(m_EventOffsetsOffset);
            var eventOffset0 = m_Reader.ReadUInt32();
            if (eventOffset0 != eventsDataOffset)
                throw new FileLoadException($"PML has mismatched first event offset ({eventOffset0} and {eventsDataOffset})", pmlPath);

            m_Strings = ReadStringData(stringsOffset);
            ReadProcessData(processesOffset);
        }

        string[] ReadStringData(ulong stringsOffset)
        {
            SeekBegin(stringsOffset);

            var stringDataOffsets = new uint[m_Reader.ReadUInt32()];
            for (var istring = 0; istring < stringDataOffsets.Length; ++istring)
                stringDataOffsets[istring] = m_Reader.ReadUInt32();
            var strings = new string[stringDataOffsets.Length];
            for (var istring = 0; istring < stringDataOffsets.Length; ++istring)
            {
                SeekBegin(stringsOffset + stringDataOffsets[istring]);
                var strlen = (int)m_Reader.ReadUInt32() / 2;
                strings[istring] = new string(m_Reader.ReadChars(strlen), 0, Math.Max(0, strlen - 1)); // drop null-term, except empty string that doesn't include one (don't know why)
            }
            
            return strings;
        }
        
        void ReadProcessData(ulong processesOffset)
        {
            SeekBegin(processesOffset);

            var processCount = (int)m_Reader.ReadUInt32();
            SeekCurrent(processCount * 4); // jump over the process indexes array
            var processDataOffsets = new uint[processCount];
            for (var iprocess = 0; iprocess < processDataOffsets.Length; ++iprocess)
                processDataOffsets[iprocess] = m_Reader.ReadUInt32();
            PmlProcess? systemProcess = null;
            for (var iprocess = 0; iprocess < processDataOffsets.Length; ++iprocess)
            {
                var processIndex = m_Reader.ReadUInt32(); // The process index (for events to use as a reference to the process)
                var processId = m_Reader.ReadUInt32(); // Process id
                
                SeekCurrent(
                    4 + // Parent process id
                    4 + // Unknown
                    8 + // Authentication id
                    4 + // Session number
                    4 + // Unknown
                    8 + // The starting time of the process.
                    8 + // The ending time of the process.
                    4 + // 1 if the process is virtualized, 0 otherwise.
                    4 + // 1 if this process is 64 bit, 0 if WOW64.
                    4 + // Integrity - as a string index
                    4); // the user - as a string index
                
                var processName = m_Strings[m_Reader.ReadUInt32()]; // the process name - as a string index
                
                SeekCurrent(
                    4 + // the image path - as a string index
                    4 + // the command line - as a string index
                    4 + // company of the executable - as a string index
                    4 + // version of the executable - as a string index
                    4 + // description of the executable - as a string index
                    4 + // Icon index small (0x10 pixels)
                    4 + // Icon index big (0x20 pixels)
                    8); // Unknown

                var moduleCount = m_Reader.ReadUInt32(); // number of modules in the process
                var totalModuleCount = moduleCount;
                if (systemProcess != null)
                    totalModuleCount += (uint)systemProcess.Modules.Count;

                var modules = new PmlModule[totalModuleCount];
                for (var imodule = 0; imodule < moduleCount; ++imodule)
                {
                    SeekCurrent(8); // Unknown
                    
                    var baseAddress = m_Reader.ReadUInt64(); // Base address of the module.
                    var size = m_Reader.ReadUInt32(); // Size of the module.
                    var imagePath = m_Strings[m_Reader.ReadUInt32()]; // image path - as a string index
                    modules[imodule] = new PmlModule(imagePath, new AddressRange(baseAddress, size));

                    SeekCurrent(
                        4 + // version of the executable - as a string index
                        4 + // company of the executable - as a string index
                        4 + // description of the executable - as a string index
                        4 + // timestamp of the executable
                        8 * 3); // Unknown
                }

                // kernel modules are loaded into every process, and procmon only records them in the System process. just drop them in each process too, and don't care about the duplication. 
                if (systemProcess != null)
                {
                    for (var i = 0; i < systemProcess.Modules.Count; ++i)
                        modules[moduleCount + i] = systemProcess.Modules[i];
                }
                
                var process = new PmlProcess(processId, processName, modules);
                m_ProcessesByPmlIndex.Add(processIndex, process); 

                // remember the System process because it contains all the kernel modules
                if (process.ProcessName == "System")
                    systemProcess = process;
            }
        }
        
        public IEnumerable<PmlProcess> Processes => m_ProcessesByPmlIndex.Values;

        // one instance is created per call, then updated and yielded on each iteration  
        public IEnumerable<PmlEventStack> SelectEventStacks(int startAtIndex = 0)
        {
            var offsets = new UInt64[m_EventCount - startAtIndex];
            SeekBegin(m_EventOffsetsOffset);
            SeekCurrent(startAtIndex * 5);
            for (var ievent = 0; ievent < offsets.Length; ++ievent)
            {
                offsets[ievent] = m_Reader.ReadUInt32();
                SeekCurrent(1); // Unknown flags
            }
            
            var eventStack = new PmlEventStack();
            
            for (var ioffset = 0; ioffset < offsets.Length; ++ioffset)
            {
                eventStack.EventIndex = ioffset + startAtIndex;
                SeekBegin(offsets[ioffset]);
             
                var processIndex = m_Reader.ReadUInt32(); // The index to the process of the event.
                SeekCurrent(4); // Thread Id.
                var eventClass = (PmlEventClass)m_Reader.ReadUInt32();
                
                if (eventClass == PmlEventClass.FileSystem)
                {
                    eventStack.Process = m_ProcessesByPmlIndex[processIndex];

                    SeekCurrent(
                        2 + // see ProcessOperation, RegistryOperation, NetworkOperation, FilesystemOperation in consts.py
                        6 + // Unknown.
                        8); // Duration of the operation in 100 nanoseconds interval.

                    eventStack.CaptureTime = (long)m_Reader.ReadUInt64(); // (FILETIME) The time when the event was captured.

                    SeekCurrent(4); // The value of the event result.
                
                    eventStack.FrameCount = m_Reader.ReadUInt16(); // The depth of the captured stack trace.
                    if (eventStack.FrameCount > 0)
                    {
                        SeekCurrent(
                            2 + // Unknown
                            4 + // The size of the specific detail structure (contains path and other details)
                            4); // The offset from the start of the event to extra detail structure (not necessarily continuous with this structure).

                        for (var iframe = 0; iframe < eventStack.FrameCount; ++iframe)
                            eventStack.Frames[iframe] = m_Reader.ReadUInt64();
                        
                        yield return eventStack;
                    }
                }
            }
        }

        public PmlProcess? FindProcessByProcessId(uint processId) =>
            m_ProcessesByPmlIndex.Values.FirstOrDefault(p => p.ProcessId == processId);            
        
        void SeekBegin(ulong offset) => m_Reader.BaseStream.Seek((long)offset, SeekOrigin.Begin); 
        void SeekCurrent(int offset) => m_Reader.BaseStream.Seek(offset, SeekOrigin.Current); 
    }
}
