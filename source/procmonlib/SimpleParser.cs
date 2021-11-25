using System;

namespace ProcMonUtils
{
    class SimpleParserException : Exception
    {
        public SimpleParserException(string message)
            : base(message) {}
    }
    
    struct SimpleParser
    {
        public string Text;
        public int Offset;

        public SimpleParser(string text, int offset = 0)
        {
            Text = text;
            Offset = offset;
        }

        public override string ToString()
        {
            return AsSpan().ToString();
        }

        public int Remain => Text.Length - Offset;
        public bool AtEnd => Remain == 0;
        public void Advance(int count) => Offset += count;
        
        public ReadOnlySpan<char> AsSpan()
        {
            return Text.AsSpan(Offset);
        }

        public int Count(char find)
        {
            var count = 0;
            for (var i = Offset; i < Text.Length; ++i)
            {
                if (Text[i] == find)
                    ++count;
            }
            return count;
        }
        
        public void Expect(char expect)
        {
            var got = Text[Offset++];
            if (got != expect)
                throw new SimpleParserException($"Expected {expect}, got {got} at offset {Offset-1} for line: {Text}");
        }
        
        public void Expect(string expect)
        {
            foreach (var c in expect)
            {
                var got = Text[Offset++]; 
                if (got != c)
                    throw new SimpleParserException($"Expected {c}, got {got} at offset {Offset-1} for line: {Text}");
            }
        }
        
        public ReadOnlySpan<char> ReadStringUntil(char terminator)
        {
            var start = Offset;
            while (Text[Offset] != terminator)
                ++Offset;
            
            return Text.AsSpan(start, Offset - start);
        }
        
        public char ReadChar()
        {
            return Text[Offset++];
        }
        
        public char PeekChar()
        {
            return Text[Offset];
        }
        
        public char PeekCharSafe()
        {
            return Offset == Text.Length ? '\0' : Text[Offset];
        }
        
        public ulong ReadULong()
        {
            var i = 0ul;
            var start = Offset;
            
            while (Offset < Text.Length)
            {
                uint c = Text[Offset];
                if (c is < '0' or > '9')
                    break;

                var old = i;
                i = 10*i + (c-'0');
                if (i < old)
                    throw new OverflowException($"Integer starting at offset {start} too big in line: {Text}");
                
                ++Offset;
            }
            
            if (start == Offset)
                throw new SimpleParserException($"Expected uint, got {Text[start]} at offset {start} for line: {Text}");
            
            return i;
        }
        
        public ulong ReadULongHex()
        {
            var i = 0ul;
            var start = Offset;
            
            while (Offset < Text.Length)
            {
                uint c = Text[Offset];
                var old = i;

                if (c >= '0' && c <= '9')
                    i = 16*i + (c-'0');
                else if (c >= 'a' && c <= 'f')
                    i = 16*i + (c-'a'+10);
                else if (c >= 'A' && c <= 'F')
                    i = 16*i + (c-'A'+10);
                else
                    break;

                if (i < old)
                    throw new OverflowException($"Integer starting at offset {start} too big in line: {Text}");
                
                ++Offset;
            }
            
            if (start == Offset)
                throw new SimpleParserException($"Expected uint, got {Text[start]} at offset {start} for line: {Text}");
            
            return i;
        }
    }
}
