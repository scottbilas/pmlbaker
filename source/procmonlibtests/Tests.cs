using System.Linq;
using System.Text.RegularExpressions;
using NiceIO;
using NUnit.Framework;
using ProcMonUtils;
using Shouldly;

class Tests
{
    NPath m_PmipPath = null!, m_PmlBakedPath = null!; 
    
    // crap tests just to get some basic sanity..
    
    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var testDataPath = TestContext.CurrentContext
            .TestDirectory.ToNPath()
            .ParentContaining("testdata", true)
            .DirectoryMustExist();

        var pmlPath = testDataPath.Combine("basic.pml");
        m_PmipPath = testDataPath.Files("pmip*.txt").Single();
        m_PmlBakedPath = pmlPath.ChangeExtension(".pmlbaked");
        
        PmlUtils.Symbolicate(pmlPath, m_PmipPath, m_PmlBakedPath);
    }
    
    [Test]
    public void PmipBasics()
    {
        var mono = new MonoSymbolReader(m_PmipPath);
        foreach (var symbol in mono.Symbols)
        {
            mono.FindSymbol(symbol.Address.Base + symbol.Address.Size / 2).ShouldBe(symbol);
            mono.FindSymbol(symbol.Address.Base).ShouldBe(symbol);
            mono.FindSymbol(symbol.Address.End - 1).ShouldBe(symbol);
        }
        
        mono.FindSymbol(mono.Symbols[0].Address.Base - 1).ShouldBeNull();
        mono.FindSymbol(mono.Symbols[mono.Symbols.Length - 1].Address.End).ShouldBeNull();
    }
    
    [Test]
    public void WriteAndParse()
    {
        var pmlQuery = new PmlQuery(m_PmlBakedPath);
        
        var frame = pmlQuery.GetRecordBySequence(36).Frames[2];
        frame.Module.ShouldBe("FLTMGR.SYS");
        frame.Type.ShouldBe(FrameType.Kernel);
        frame.Symbol.ShouldBe("FltGetFileNameInformation");
        frame.Offset.ShouldBe(0x992);
    }
    
    [Test]
    public void Match()
    {
        var pmlQuery = new PmlQuery(m_PmlBakedPath);
        
        // find all events where someone is calling a dotnet generic
        var matches = pmlQuery
            .MatchRecordsBySymbol(new Regex("`"))
            .OrderBy(seq => seq)
            .Select(seq => pmlQuery.GetRecordBySequence(seq))
            .ToList();
        
        matches.First().Sequence.ShouldBe(3);
        matches.Last().Sequence.ShouldBe(311);
    }
}
