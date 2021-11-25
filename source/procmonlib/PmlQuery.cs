using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ProcMonUtils
{
    public struct EventRecord
    {
        public int Sequence;
        public DateTime CaptureTime;
        public int ProcessId;
        public FrameRecord[] Frames;
    }
    
    public enum FrameType
    {
        Kernel,
        User,
        Mono,
    }
    
    public struct FrameRecord
    {
        public FrameType Type;
        public int ModuleStringIndex;
        public int SymbolStringIndex;
        public ulong Offset; // will be the full address if no symbol
    }
    
    public class PmlQuery
    {
        EventRecord[] m_Events;
        List<string> m_Strings = new();
        Dictionary<DateTime, int> m_EventsByTime = new();
        Dictionary<string, List<int>> m_SymbolsToEvents = new();
        Dictionary<string, List<int>> m_ModulesToEvents = new();

        enum State { Seeking, Config, Events, Strings }
        
        public PmlQuery(string pmlBakedPath)
        {
            Load(pmlBakedPath);

            if (m_Events == null)
                throw new FileLoadException($"No events found in {pmlBakedPath}");
            
            foreach (var evt in m_Events)
            {
                // uninitialized events will happen when there are gaps in the sequencing 
                if (evt.ProcessId == 0)
                    continue;
                
                foreach (var frame in evt.Frames)
                {
                    Add(m_ModulesToEvents, evt.Sequence, frame.ModuleStringIndex);
                    Add(m_SymbolsToEvents, evt.Sequence, frame.SymbolStringIndex);
                }
            }
        }

        public string GetString(int stringIndex) => m_Strings[stringIndex]; 
        
        void Add(Dictionary<string, List<int>> dict, int eventIndex, int stringIndex)
        {
            var value = GetString(stringIndex);
            if (!dict.TryGetValue(value, out var list))
                dict.Add(value, list = new());
            list.Add(eventIndex);
        }

        void Load(string pmlBakedPath)
        {
            var lines = File
                .ReadLines(pmlBakedPath)
                .Select((text, index) => (text: text.Trim(), index + 1))
                .Where(l => l.text.Length != 0 && l.text[0] != '#');
            
            var (state, currentLine) = (State.Seeking, 0);

            try
            {
                foreach (var (line, index) in lines)
                {
                    currentLine = index;
                    
                    if (line[0] == '[')
                    {
                        state = line switch
                        {
                            "[Config]" => State.Config,
                            "[Events]" => State.Events,
                            "[Strings]" => State.Strings,
                            _ => throw new Exception($"Not a supported section {line}")
                        };
                        continue;
                    }
                    
                    switch (state)
                    {
                        case State.Seeking:
                            throw new Exception("Unexpected lines without category");
                        
                        case State.Config:
                            var m = Regex.Match(line, @"(\w+)\s*=\s*(\w+)");
                            if (!m.Success)
                                throw new Exception($"Unexpected config format: {line}");
                            switch (m.Groups[1].Value)
                            {
                                case "EventCount":
                                    var eventCount = int.Parse(m.Groups[2].Value);
                                    m_Events = new EventRecord[eventCount];
                                    break;
                                case "DebugFormat":
                                    if (bool.Parse(m.Groups[2].Value))
                                        throw new Exception("DebugFormat=true not supported for querying");
                                    break;
                                default:
                                    throw new Exception($"Unexpected config option: {m.Groups[1].Value}");
                            }
                            break;
                        
                        case State.Events:
                            ParseEventLine(line);
                            break;
                        
                        case State.Strings:
                            var parser = new SimpleParser(line);
                            var stringIndex = (int)parser.ReadULongHex();
                            if (stringIndex != m_Strings.Count)
                                throw new InvalidOperationException("Mismatch string index");
                            parser.Expect(':');
                            m_Strings.Add(parser.AsSpan().ToString());
                            break;
                    }
                }
            }
            catch (Exception x)
            {
                throw new FileLoadException($"{pmlBakedPath}({currentLine}): {x.Message}", x);
            }
        }

        void ParseEventLine(string line)
        {
            var parser = new SimpleParser(line);
            
            var sequence = (int)parser.ReadULongHex();
            ref var eventRecord = ref m_Events[sequence];
            eventRecord.Sequence = sequence;
            parser.Expect(':');
            
            eventRecord.CaptureTime = DateTime.FromFileTime((long)parser.ReadULongHex());
            m_EventsByTime[eventRecord.CaptureTime] = sequence;
            parser.Expect(';');
            
            eventRecord.ProcessId = (int)parser.ReadULongHex();
            eventRecord.Frames = new FrameRecord[parser.Count(';')];

            for (var iframe = 0; iframe < eventRecord.Frames.Length; ++iframe)
            {
                parser.Expect(';');

                var typec = parser.ReadChar();
                var type = typec switch
                {
                    'K' => FrameType.Kernel,
                    'U' => FrameType.User,
                    'M' => FrameType.Mono,
                    _ => throw new ArgumentOutOfRangeException($"Unknown type '{typec}' for frame {iframe}")
                };
                
                parser.Expect(',');
                var first = parser.ReadULongHex();

                switch (parser.PeekCharSafe())
                {
                    // non-symbol frame
                    case ';':
                    case '\0':
                        eventRecord.Frames[iframe] = new FrameRecord
                            { Type = type, Offset = first, };
                        break;
                    
                    // symbol frame
                    case ',':
                        ref var frameRecord = ref eventRecord.Frames[iframe];
                        frameRecord.ModuleStringIndex = (int)first;
                        parser.Advance(1); // already read
                        frameRecord.SymbolStringIndex = (int)parser.ReadULongHex();
                        parser.Expect(',');
                        frameRecord.Offset = parser.ReadULongHex();
                        break;
                    
                    default:
                        throw new Exception("Parse error");
                }
            }
            
            if (!parser.AtEnd)
                throw new Exception("Unexpected extra frames");
        }
        
        public IReadOnlyList<EventRecord> AllRecords => m_Events;
        
        public EventRecord GetRecordBySequence(int sequence) => m_Events[sequence];
        public EventRecord? FindRecordByCaptureTime(DateTime dateTime) => m_EventsByTime.TryGetValue(dateTime, out var foundIndex) ? m_Events[foundIndex] : null;

        static IEnumerable<int> MatchRecordsByText(IEnumerable<KeyValuePair<string, List<int>>> items, Regex regex) =>
            items.Where(kv => regex.IsMatch(kv.Key)).SelectMany(kv => kv.Value).Distinct();
        
        public IEnumerable<int> MatchRecordsBySymbol(Regex regex) => MatchRecordsByText(m_SymbolsToEvents, regex);
        public IEnumerable<int> MatchRecordsByModule(Regex regex) => MatchRecordsByText(m_ModulesToEvents, regex);
    }
}
